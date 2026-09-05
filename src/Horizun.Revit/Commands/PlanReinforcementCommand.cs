// -----------------------------------------------------------------------------
// Horizun Revit MCP - read-only reinforcement planning.
// Original Horizun code. This command opens no transaction and writes nothing.
//
// It answers, before anything is committed: which hosts does this requirement
// set actually reach, which bar type and shape do its names resolve to, how many
// bars does each layout produce, where does every bar POSITION land, and does
// the set fit inside its host.
//
// The last one is the reason this exists. Revit creates a set longer than its
// beam without complaint - correct element, correct host, correct type, and some
// of the steel standing outside the concrete. Nothing in the reply separates
// that from a correct set, so it is separated here, where nothing has been
// written yet.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanReinforcementCommand : ICommand
    {
        public string Name => "horizun_plan_reinforcement";
        public string Description =>
            "Resolve a structural requirement set against the model and report what it would build, including " +
            "every bar position and whether each set fits its host. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            // The host boundaries are cached for the length of ONE command, and a
            // host somebody resized between the plan and the apply must be measured
            // again rather than remembered.
            ReinforcementResolver.ForgetMeshes();

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail("requirement_set is required and must be an object. " +
                                          "Schema: " + StructuralRequirementSet.SchemaName + ".");

            StructuralRequirementSet set = StructuralRequirementSet.Load(setJson);
            if (!set.Ok)
                return CommandResult.FailWithDetail(
                    "The requirement set was refused, so nothing was planned: " + set.Error,
                    StructuralRequirementSet.RefusalDetail(set));

            var narrow = new List<long>();
            foreach (JToken t in request["host_ids"] as JArray ?? new JArray())
            {
                long v = t.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(v)) return CommandResult.Fail("host_ids carries a value that is not an ElementId.");
                narrow.Add(v);
            }

            List<ResolvedCoverRow> covers = ReinforcementResolver.ResolveCover(doc, set, narrow);
            // The plan is the rehearsal a person reads before approving a write, so
            // it asks the write path's question: has this rule already built here?
            List<ResolvedRebarRow> bars = ReinforcementResolver.ResolveRebar(doc, set, narrow,
                                                                             refuseAlreadyBuilt: true);

            var coverRows = new JArray();
            for (int i = 0; i < covers.Count; i++) coverRows.Add(ReinforcementResolver.DescribeCoverRow(covers[i], i));
            var barRows = new JArray();
            for (int i = 0; i < bars.Count; i++) barRows.Add(ReinforcementResolver.DescribeRebarRow(bars[i], i));

            int buildable = bars.Count(b => b.Ok);
            int refusedRequired = bars.Count(b => !b.Ok && b.Rule != null && b.Rule.Required)
                                + covers.Count(c => !c.Ok && c.Rule != null && c.Rule.Required);
            int totalBars = bars.Where(b => b.Ok).Sum(b => b.Layout.Quantity);
            double totalSteelMm = bars.Where(b => b.Ok).Sum(b => b.ExpectedBarLengthMm * b.Layout.Quantity);

            var byCode = new JObject();
            foreach (var g in bars.Where(b => !b.Ok).GroupBy(b => b.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                byCode[g.Key] = g.Count();
            foreach (var g in covers.Where(c => !c.Ok).GroupBy(c => c.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                byCode[g.Key] = (byCode[g.Key] == null ? 0 : (int)byCode[g.Key]) + g.Count();

            var result = new JObject
            {
                ["requirement_set"] = new JObject
                {
                    ["id"] = set.Id,
                    ["version"] = set.Version,
                    ["title"] = set.Title,
                    ["sha256"] = StructuralRequirementSet.Sha256Of(setJson),
                    ["cover_rules"] = set.CoverRules.Count,
                    ["reinforcement_rules"] = set.RebarRules.Count,
                    ["stirrup_zone_rules"] = set.StirrupZoneRules.Count,
                    ["mat_rules"] = set.MatRules.Count
                },
                ["tolerances_mm"] = new JObject
                {
                    ["length"] = set.Tolerances.LengthMm,
                    ["spacing"] = set.Tolerances.SpacingMm,
                    ["cover"] = set.Tolerances.CoverMm
                },
                ["cover"] = coverRows,
                ["reinforcement"] = barRows,
                ["summary"] = new JObject
                {
                    ["rows"] = bars.Count + covers.Count,
                    ["rebar_rows_buildable"] = buildable,
                    ["rebar_rows_refused"] = bars.Count - buildable,
                    ["cover_rows_to_set"] = covers.Count(c => c.Ok && !c.AlreadyRight),
                    ["cover_rows_already_right"] = covers.Count(c => c.Ok && c.AlreadyRight),
                    ["cover_rows_refused"] = covers.Count(c => !c.Ok),
                    ["required_rows_refused"] = refusedRequired,
                    ["expected_bars"] = totalBars,
                    ["expected_steel_length_mm"] = Math.Round(totalSteelMm, 3),
                    ["expected_steel_length_means"] =
                        "declared centrelines multiplied by the bar count. It EXCLUDES hook length, which Revit " +
                        "adds itself, so the model will report more wherever a hook is declared. It is what the " +
                        "plan asked for, never a takeoff.",
                    ["refusals_by_code"] = byCode
                },
                ["would_apply"] = refusedRequired == 0 && (buildable > 0 || covers.Any(c => c.Ok && !c.AlreadyRight)),
                ["would_apply_means"] =
                    "true when nothing REQUIRED was refused and there is something to do. A rule marked " +
                    "required: false may be refused without stopping the rest - that is what the key is for.",
                ["refusal_codes"] = new JArray(ReinforcementResolver.AllCodes),
                ["refusal_codes_mean"] =
                    "the CLOSED set of codes a refused row can carry, published here rather than described in " +
                    "the tool text - so it cannot drift from the code that emits it.",
                ["writes_nothing"] = true,
                ["next"] = "Send the same requirement_set to horizun_apply_reinforcement with dry_run=true to " +
                           "obtain a confirmation token, then again with dry_run=false and that token."
            };
            return CommandResult.Ok(result);
        }
    }
}
