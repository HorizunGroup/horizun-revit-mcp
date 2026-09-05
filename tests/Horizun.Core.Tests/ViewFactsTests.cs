// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Views, proved by running the rules. The whole file is about one thing: five
// different reasons a property has no finding, and none of the four empty ones
// is a pass.
//
//   not_applicable   a legend has no level
//   not_readable     the property exists and the read threw
//   not_requested    the profile said nothing
//   ok / failed      a rule actually ran
//
// A tool that collapses the first two into "failed" reports Revit rather than
// the model, and the reader stops believing any of it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewFactsTests
    {
        private static readonly string[] Known =
        {
            "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan", "Elevation", "Section",
            "Detail", "ThreeD", "DraftingView", "Legend", "Schedule", "Rendering", "Walkthrough",
            "ProjectBrowser", "Internal"
        };

        private static ViewProfile P(string json) => ViewFactsRules.Read(JToken.Parse(json), Known);

        private static ViewStateFact V(string type, string name = "V1")
        {
            return new ViewStateFact { ElementId = 1, Name = name, ViewType = type };
        }

        private static string StatusOf(List<ViewPropertyVerdict> vs, string prop) =>
            vs.Single(v => v.Property == prop).Status;

        // -------------------------------------------------- applicability

        [Fact]
        public void A_legend_has_no_level_and_that_is_not_a_failure()
        {
            // THE ONE THAT MATTERS. A legend reported as "view with no level" is a
            // finding about Revit, not about the model.
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(V("Legend"), ViewFactsRules.Read(null, Known));
            Assert.Equal(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.Level));
            Assert.Equal(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.ViewRange));
        }

        [Fact]
        public void A_schedule_has_no_crop_and_that_is_not_a_failure()
        {
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(V("Schedule"), ViewFactsRules.Read(null, Known));
            Assert.Equal(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.CropActive));
            Assert.Equal(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.Scale));
            // But a schedule CAN take a template and CAN sit on a sheet.
            Assert.NotEqual(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.Template));
            Assert.NotEqual(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.OnSheet));
        }

        [Fact]
        public void A_drafting_view_has_no_view_range_but_does_have_a_scale()
        {
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(V("DraftingView"), ViewFactsRules.Read(null, Known));
            Assert.Equal(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.ViewRange));
            Assert.NotEqual(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.Scale));
        }

        [Fact]
        public void A_plan_has_every_property_this_area_knows_about()
        {
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(V("FloorPlan"), ViewFactsRules.Read(null, Known));
            foreach (string prop in ViewProperties.All)
                Assert.NotEqual(ViewPropertyStatus.NotApplicable, StatusOf(vs, prop));
        }

        [Fact]
        public void Revits_own_internal_views_are_recognised_as_internal()
        {
            Assert.True(ViewApplicability.IsInternal("ProjectBrowser"));
            Assert.True(ViewApplicability.IsInternal("Internal"));
            Assert.False(ViewApplicability.IsInternal("FloorPlan"));
        }

        [Fact]
        public void The_reply_explains_that_not_applicable_is_not_a_pass()
        {
            Assert.Contains("NOT a pass", ViewFactsRules.ApplicabilityMeans);
            Assert.Contains("not_readable", ViewFactsRules.ApplicabilityMeans);
            Assert.Contains("is not a view without a template", ViewFactsRules.TemplatesMean);
        }

        // ---------------------------------------------------- not readable

        [Fact]
        public void A_property_whose_read_threw_is_not_readable_and_not_a_failure()
        {
            // Unknown is not non-compliant. The property exists on this view type,
            // so not_applicable would be wrong too.
            //
            // TEMPLATE is the property that proves the guard carries weight. Its
            // backing field is a plain bool, so without the unreadable check a view
            // whose template read THREW arrives at the rule as "no template
            // assigned" and is reported FAILED - a finding invented out of a read
            // that never returned. (Scale would not show this: its own check
            // happens to handle the null and reaches not_readable by another road,
            // which is exactly why a mutation of the guard came back vacuous
            // against it.)
            ViewStateFact f = V("FloorPlan");
            f.Unreadable.Add(ViewProperties.Template);
            f.TemplateAssigned = false;
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(f, P(
                @"{ ""version"": ""v1"", ""FloorPlan"": { ""template_required"": true } }"));
            Assert.Equal(ViewPropertyStatus.NotReadable, StatusOf(vs, ViewProperties.Template));

            ViewStateFact g = V("FloorPlan");
            g.Unreadable.Add(ViewProperties.Scale);
            Assert.Equal(ViewPropertyStatus.NotReadable, StatusOf(
                ViewFactsRules.Judge(g, P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""allowed_scales"": [50] } }")),
                ViewProperties.Scale));
        }

        [Fact]
        public void Not_applicable_wins_over_not_readable_because_the_property_does_not_exist()
        {
            ViewStateFact f = V("Legend");
            f.Unreadable.Add(ViewProperties.Level);
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(f, ViewFactsRules.Read(null, Known));
            Assert.Equal(ViewPropertyStatus.NotApplicable, StatusOf(vs, ViewProperties.Level));
        }

        // ------------------------------------------------------- judgement

        [Fact]
        public void With_no_profile_every_applicable_property_is_not_requested()
        {
            ViewProfile p = ViewFactsRules.Read(null, Known);
            Assert.True(p.Absent);
            Assert.Contains("NOT a pass", p.Message);

            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(V("FloorPlan"), p);
            Assert.All(vs, v => Assert.NotEqual(ViewPropertyStatus.Ok, v.Status));
            Assert.All(vs, v => Assert.NotEqual(ViewPropertyStatus.Failed, v.Status));
        }

        [Fact]
        public void A_view_without_its_required_template_fails()
        {
            ViewStateFact f = V("FloorPlan");
            f.TemplateAssigned = false;
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(f, P(
                @"{ ""version"": ""v1"", ""FloorPlan"": { ""template_required"": true } }"));
            Assert.Equal(ViewPropertyStatus.Failed, StatusOf(vs, ViewProperties.Template));
        }

        [Fact]
        public void A_view_type_the_profile_is_silent_about_is_not_requested()
        {
            ViewStateFact f = V("Section");
            f.TemplateAssigned = false;
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(f, P(
                @"{ ""version"": ""v1"", ""FloorPlan"": { ""template_required"": true } }"));
            Assert.Equal(ViewPropertyStatus.NotRequested, StatusOf(vs, ViewProperties.Template));
        }

        [Fact]
        public void A_scale_outside_the_allowed_list_fails_and_one_inside_passes()
        {
            ViewProfile p = P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""allowed_scales"": [50, 100] } }");
            ViewStateFact good = V("FloorPlan"); good.Scale = 50;
            ViewStateFact bad = V("FloorPlan"); bad.Scale = 25;
            Assert.Equal(ViewPropertyStatus.Ok, StatusOf(ViewFactsRules.Judge(good, p), ViewProperties.Scale));
            Assert.Equal(ViewPropertyStatus.Failed, StatusOf(ViewFactsRules.Judge(bad, p), ViewProperties.Scale));
        }

        [Fact]
        public void A_required_crop_that_is_off_fails_and_a_forbidden_crop_that_is_on_fails()
        {
            ViewStateFact on = V("FloorPlan"); on.CropActive = true;
            ViewStateFact off = V("FloorPlan"); off.CropActive = false;
            Assert.Equal(ViewPropertyStatus.Failed, StatusOf(
                ViewFactsRules.Judge(off, P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""crop_required"": true } }")),
                ViewProperties.CropActive));
            Assert.Equal(ViewPropertyStatus.Failed, StatusOf(
                ViewFactsRules.Judge(on, P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""crop_required"": false } }")),
                ViewProperties.CropActive));
        }

        [Fact]
        public void A_required_filter_that_is_missing_fails_and_names_it()
        {
            ViewStateFact f = V("FloorPlan");
            f.Filters.Add("Present");
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(f, P(
                @"{ ""version"": ""v1"", ""FloorPlan"": { ""required_filters"": [""Absent""] } }"));
            ViewPropertyVerdict v = vs.Single(x => x.Property == ViewProperties.Filters);
            Assert.Equal(ViewPropertyStatus.Failed, v.Status);
            Assert.Contains("Absent", v.Detail);
        }

        [Fact]
        public void A_view_required_on_a_sheet_and_placed_on_none_fails()
        {
            ViewStateFact f = V("FloorPlan");
            f.PlacedOnSheet = false;
            Assert.Equal(ViewPropertyStatus.Failed, StatusOf(
                ViewFactsRules.Judge(f, P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""on_sheet_required"": true } }")),
                ViewProperties.OnSheet));
        }

        [Fact]
        public void An_explicit_exception_makes_every_property_not_requested_again()
        {
            ViewStateFact f = V("FloorPlan", "Legacy Plan");
            f.TemplateAssigned = false;
            List<ViewPropertyVerdict> vs = ViewFactsRules.Judge(f, P(
                @"{ ""version"": ""v1"",
                    ""FloorPlan"": { ""template_required"": true, ""exceptions"": [""Legacy Plan""] } }"));
            ViewPropertyVerdict v = vs.Single(x => x.Property == ViewProperties.Template);
            Assert.Equal(ViewPropertyStatus.NotRequested, v.Status);
            Assert.Contains("explicit exception", v.Detail);
        }

        // ----------------------------------------------------------- tally

        [Fact]
        public void The_tally_keeps_the_five_statuses_apart()
        {
            ViewStateFact plan = V("FloorPlan"); plan.TemplateAssigned = false;
            var all = new List<List<ViewPropertyVerdict>>
            {
                ViewFactsRules.Judge(plan, P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""template_required"": true } }")),
                ViewFactsRules.Judge(V("Legend"), ViewFactsRules.Read(null, Known))
            };
            JObject t = ViewFactsRules.Tally(all);
            Assert.True(t.Value<long>("failed") >= 1);
            Assert.True(t.Value<long>("not_applicable") >= 1);
            Assert.True(t.Value<long>("not_requested") >= 1);
        }

        // -------------------------------------------------------- refusals

        [Fact]
        public void A_profile_naming_a_view_type_this_revit_does_not_have_is_refused()
        {
            // A rule filed under a misspelt type never runs and reports every view
            // as acceptable.
            ViewProfile p = P(@"{ ""version"": ""v1"", ""FloorPlanz"": { ""template_required"": true } }");
            Assert.False(p.Ok);
            Assert.Equal(ViewProfileCodes.UnknownViewType, p.Code);
            Assert.Contains("FloorPlanz", p.Message);
        }

        [Fact]
        public void A_profile_without_a_version_is_refused()
        {
            Assert.Equal(ViewProfileCodes.NoVersion,
                P(@"{ ""FloorPlan"": { ""template_required"": true } }").Code);
        }

        [Fact]
        public void An_unknown_rule_key_is_refused()
        {
            Assert.Equal(ViewProfileCodes.UnknownKey,
                P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""template_requiredd"": true } }").Code);
        }

        [Fact]
        public void A_scale_list_with_a_nonsense_entry_is_refused()
        {
            Assert.Equal(ViewProfileCodes.BadRule,
                P(@"{ ""version"": ""v1"", ""FloorPlan"": { ""allowed_scales"": [0] } }").Code);
        }

        [Fact]
        public void A_refused_profile_is_not_applied_even_though_it_parsed_earlier_types()
        {
            ViewProfile p = P(@"{ ""version"": ""v1"",
                                  ""FloorPlan"": { ""template_required"": true },
                                  ""Nonsense"": { ""template_required"": true } }");
            Assert.False(p.Ok);
            ViewStateFact f = V("FloorPlan");
            f.TemplateAssigned = false;
            Assert.Equal(ViewPropertyStatus.NotRequested,
                StatusOf(ViewFactsRules.Judge(f, p), ViewProperties.Template));
        }
    }
}
