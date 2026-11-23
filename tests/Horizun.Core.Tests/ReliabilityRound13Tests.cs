using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReliabilityRound13Tests
    {
        [Theory]
        [InlineData("unchanged")]
        [InlineData("changed_requested_resolution")]
        [InlineData("added_unrequested_field")]
        public void Schedule_plan_rechecks_the_field_identity_after_an_earlier_child_runs(string scenario)
        {
            bool drift = scenario == "changed_requested_resolution";
            var doc = new Document { Title = "Schedule plan", FixtureConfirmations = new ConfirmationStore() };
            var transactions = new List<string>();
            doc.TestTransactionStarted = transactions.Add;
            doc.CaptureFixtureState = () =>
            {
                long id = doc.ScheduleParameterId;
                return () => doc.ScheduleParameterId = id;
            };
            var schedule = new CreateScheduleCommand();
            var plan = PlanWith(schedule, () =>
            {
                if (drift) doc.ScheduleParameterId = 22;
                if (scenario == "added_unrequested_field") doc.UnrelatedScheduleFieldAdded = true;
            });
            var request = Graph(schedule, new JObject
            {
                ["category"] = "OST_MultiCategory", ["name"] = "Approved schedule",
                ["fields"] = new JArray("Review field"), ["itemized"] = true, ["include_links"] = false
            });

            CommandResult applied = Apply(plan, doc, request);

            // Outer rehearsal, apply recheck and late probe all create and roll back
            // their temporary schedules. Only a matching plan starts the final write.
            Assert.Equal(3, transactions.FindAll(name => name == "Horizun: rehearse schedule fields").Count);
            if (!drift)
            {
                Assert.True(applied.Success, applied.Error);
                Assert.True(doc.Assimilated);
                Assert.Equal("Horizun: create schedule", transactions[3]);
                Assert.Equal(4, transactions.Count);
                var created = Assert.IsType<ViewSchedule>(Assert.Single(doc.Elements).Value);
                Assert.Equal("Approved schedule", created.Name);
                var field = created.Definition.GetField(Assert.Single(created.Definition.GetFieldOrder()));
                Assert.Equal(21, field.ParameterId.Value);
                return;
            }

            Assert.False(applied.Success);
            AssertChildRefusedAndGroupRolledBack(applied.Detail, "schedule creation");
            Assert.Equal(3, transactions.Count);
            Assert.Equal(21, doc.ScheduleParameterId);
            Assert.Empty(doc.Elements);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Graphics_copy_in_an_atomic_plan_binds_the_actual_source_overrides(bool drift)
        {
            var doc = new Document { Title = "Graphics plan", FixtureConfirmations = new ConfirmationStore() };
            var source = new View { Id = new ElementId(10), ElementTransparency = 17 };
            var target = new View { Id = new ElementId(11) };
            var element = new Wall { Id = new ElementId(12), OwnerViewId = ElementId.InvalidElementId };
            doc.Elements.Add(10, source);
            doc.Elements.Add(11, target);
            doc.Elements.Add(12, element);
            doc.CaptureFixtureState = () =>
            {
                int transparency = source.ElementTransparency;
                return () => source.ElementTransparency = transparency;
            };
            var graphics = new ViewVisibilityCommand();
            var plan = PlanWith(graphics, () => { if (drift) source.ElementTransparency = 37; });
            var request = Graph(graphics, new JObject
            {
                ["operation"] = "copy_view_graphics", ["view_id"] = 10, ["view_ids"] = new JArray(11),
                ["include"] = new JArray("elements"), ["element_ids"] = new JArray(12)
            });

            CommandResult applied = Apply(plan, doc, request);

            Assert.Equal(drift ? 0 : 1, doc.TransactionStarts);
            Assert.Equal(drift ? 0 : 1, target.OverrideWrites);
            Assert.Equal(drift ? 0 : 17, target.ElementTransparency);
            // Rehearsal, recheck, apply planning, then both verifications if applied.
            Assert.Equal(drift ? 3 : 5, target.OverrideReads);
            if (!drift)
            {
                Assert.True(applied.Success, applied.Error);
                Assert.True(doc.Assimilated);
                return;
            }

            Assert.False(applied.Success);
            AssertChildRefusedAndGroupRolledBack(applied.Detail, "view graphics copy");
            Assert.Equal(17, source.ElementTransparency);
        }

        [Fact]
        public void Document_batch_returns_the_graphics_child_refusal_and_closes_its_copy()
        {
            var app = new UIApplication();
            Document model = null;
            View source = null, target = null;
            Document OpenFixture()
            {
                model = new Document { FixtureConfirmations = new ConfirmationStore() };
                source = new View { Id = new ElementId(10), ElementTransparency = 17 };
                target = new View { Id = new ElementId(11) };
                model.Elements.Add(10, source);
                model.Elements.Add(11, target);
                model.Elements.Add(12, new Wall { Id = new ElementId(12), OwnerViewId = ElementId.InvalidElementId });
                return model;
            }

            var graphics = new ViewVisibilityCommand();
            var session = new DocumentSessionCommand(Resolve(graphics, () => source.ElementTransparency = 37), OpenFixture);
            JObject graph = Graph(graphics, new JObject
            {
                ["operation"] = "copy_view_graphics", ["view_id"] = 10, ["view_ids"] = new JArray(11),
                ["include"] = new JArray("elements"), ["element_ids"] = new JArray(12)
            });
            // The batch uses its actual file fingerprinting and command orchestration.
            // The fixture substitutes Revit's file reader with a new model on each open.
            string path = OperatingSystem.IsWindows()
                ? Path.Combine(Path.GetTempPath(), "r13-" + Guid.NewGuid().ToString("N") + ".rvt")
                : @"C:\fixtures\r13-batch-graphics.rvt";
            if (OperatingSystem.IsWindows()) File.WriteAllText(path, "Batch graphics fixture");
            try
            {
                JObject request = new JObject { ["operation"] = "batch", ["models"] = new JArray(new JObject
                    { ["file_path"] = path, ["close"] = "discard", ["actions"] = graph["actions"] }) };
                CommandResult preview = session.RunFixtureBatch(app, request);
                Assert.True(preview.Success, preview.Error);
                request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
                request["dry_run"] = false;
                request["idempotency_key"] = "r13-batch-apply";

                CommandResult result = session.RunFixtureBatch(app, request);

                Assert.False(result.Success);
                JObject row = Assert.IsType<JObject>(Assert.Single((JArray)result.Detail["models"]));
                Assert.False(row.Value<bool>("success"));
                AssertChildRefusedAndGroupRolledBack(
                    Assert.IsType<JObject>(row["steps_apply"]["detail"]), "view graphics copy");
                Assert.True(row["close"].Value<bool>("success"));
                Assert.False(model.IsValidObject);
                Assert.Empty(app.Application.Documents);
                Assert.Equal(0, model.TransactionStarts);
                Assert.Equal(0, target.OverrideWrites);
            }
            finally
            {
                if (OperatingSystem.IsWindows()) File.Delete(path);
            }
        }

        [Fact]
        public void Dispatcher_requests_and_services_exit_recovery_with_both_queues_empty()
        {
            var dispatcher = new Dispatcher { HostWork = Dispatcher.MarkExiting };
            dispatcher.Initialize();
            var app = new UIApplication();
            ExternalEvent host = dispatcher.HostEvent;
            var timeout = TimeSpan.FromSeconds(5);

            Assert.Equal(ExternalEventRequest.Accepted, host.Raise());
            Assert.True(host.ServiceNext(app, timeout));
            // Before any new client call, the real dispatcher requests a callback
            // even though neither queue has work for it.
            Assert.True(host.ServiceNext(app, timeout));
            Assert.Equal(1, dispatcher.WorkCalls);
            Assert.True(dispatcher.Invoke("horizun_health", "{}", 0).Success);

            Assert.Equal(ExternalEventRequest.Accepted, host.Raise());
            host.DenyNextRaise();
            Assert.True(host.ServiceNext(app, timeout));
            // A transient Denied is retried by the normal callback scheduler.
            Assert.True(host.ServiceNext(app, timeout));
            Assert.Equal(2, dispatcher.WorkCalls);
            Assert.True(dispatcher.Invoke("horizun_health", "{}", 0).Success);
        }

        internal static ExecutePlanCommand PlanWith(ICommand child, Action change) =>
            new ExecutePlanCommand(Resolve(child, change));

        private static Func<string, ICommand> Resolve(ICommand child, Action change)
        {
            var earlier = new EarlierAction(change);
            return name => name == child.Name ? child : name == earlier.Name ? earlier : null;
        }

        internal static JObject Graph(ICommand target, JObject args) => new JObject
        {
            ["actions"] = new JArray(
                new JObject { ["key"] = "earlier", ["tool"] = "horizun_write_params_verified", ["arguments"] = new JObject() },
                new JObject { ["key"] = "checked", ["tool"] = target.Name, ["arguments"] = args })
        };

        internal static CommandResult Apply(ExecutePlanCommand plan, Document doc, JObject request, Action<JObject> inspectPreview = null)
        {
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            CommandResult preview = plan.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            var data = Assert.IsType<JObject>(preview.Data);
            inspectPreview?.Invoke(data);
            var token = data.Value<string>("confirmation_token");
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.False(doc.Assimilated);
            request["dry_run"] = false;
            request["confirmation_token"] = token;
            request["idempotency_key"] = "r13-plan-apply";
            return plan.Execute(app, request.ToString());
        }

        internal static void AssertChildRefusedAndGroupRolledBack(JObject detail, string description)
        {
            Assert.Contains(description, detail.Value<string>("error"));
            Assert.True(detail.Value<bool>("rollback_confirmed"));
            Assert.Equal("checked", detail["failed_action"].Value<string>("key"));
            var child = Assert.IsType<JObject>(detail["failed_action"]["child_detail"]);
            Assert.Equal("stale_plan", child.Value<string>("state"));
            Assert.True(JToken.DeepEquals(new JValue(false), child["write_started"]));
            Assert.True(JToken.DeepEquals(new JValue(false), child["changes_applied"]));
        }

        // Only the preceding mutation is simulated. Its dry runs leave the fact
        // stable, so the production plan gets as far as the actual child apply.
        private sealed class EarlierAction : ICommand
        {
            private readonly Action change;
            internal EarlierAction(Action change) { this.change = change; }
            public string Name => "horizun_write_params_verified";
            public string Description => "Change a fixture fact after the plan recheck.";
            public CommandResult Execute(UIApplication app, string json)
            {
                if (JObject.Parse(json).Value<bool>("dry_run"))
                {
                    var preview = new JObject { ["dry_run"] = true };
                    ApplicationOutcome.StampRehearsal(preview, 1, 0, 0, 0);
                    return CommandResult.Ok(preview);
                }
                change();
                var applied = new JObject();
                ApplicationOutcome.Stamp(applied, WriteTally.OneObject("Committed", true));
                return CommandResult.Ok(applied);
            }
        }
    }
}
