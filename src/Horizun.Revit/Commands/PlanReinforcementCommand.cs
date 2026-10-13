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
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
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

            // ONE PIECE (L10): with `chunk`, the set is cut by level or host type and only the
            // asked piece is planned. The reply names the cut, so every later call of the loop
            // can quote it.
            ChunkOutcome chunk = ReinforcementChunking.Resolve(doc, request, setJson, set, partitionRequired: false);
            if (chunk != null && !chunk.Ok) return chunk.Refusal;
            if (chunk != null) set = chunk.PieceSet;

            List<ResolvedCoverRow> covers = ReinforcementResolver.ResolveCover(doc, set, narrow);
            // The plan is the rehearsal a person reads before approving a write, so
            // it asks the write path's question: has this rule already built here?
            // A DECLARED CROSSING ORDER (etapa 1c) displaces the sets that give way before anything is measured.
            CrossingDecision crossing;
            List<ResolvedRebarRow> bars = ReinforcementResolver.ResolveRebarCrossed(doc, set, narrow,
                                                                                    refuseAlreadyBuilt: true, decision: out crossing);
            // BARS OF DIFFERENT RULES against each other (overlapping_bar), before anything is written: a set
            // whose bars pass through another's - or come closer than the set declares - is refused here.
            OverlapReport overlap = ReinforcementResolver.RefuseOverlaps(bars, set);
            // AREA, PATH AND FABRIC (acero 3.0, L4b): the same resolver discipline, the same
            // write-path question.
            List<ResolvedSystemRow> systems = SystemReinforcementResolver.Resolve(doc, set, narrow,
                                                                                  refuseAlreadyBuilt: true);

            var coverRows = new JArray();
            for (int i = 0; i < covers.Count; i++) coverRows.Add(ReinforcementResolver.DescribeCoverRow(covers[i], i));
            var barRows = new JArray();
            for (int i = 0; i < bars.Count; i++) barRows.Add(ReinforcementResolver.DescribeRebarRow(bars[i], i));
            var areaRows = new JArray();
            var pathRows = new JArray();
            var fabricRows = new JArray();
            for (int i = 0; i < systems.Count; i++)
            {
                JObject d = SystemReinforcementResolver.Describe(systems[i], i);
                (systems[i].Kind == SystemReinforcementRules.KindArea ? areaRows
                 : systems[i].Kind == SystemReinforcementRules.KindPath ? pathRows : fabricRows).Add(d);
            }
            int systemsBuildable = systems.Count(x => x.Ok);

            // DECLARED HOOK LENGTHS (hook_types[].lengths_mm). A type property: the apply writes
            // it to the bar type, so every pair it would change is listed here, in words.
            List<ResolvedHookLength> hookLengths = RebarHookLengths.Resolve(doc, set);
            var typeChanges = new JArray();
            foreach (ResolvedHookLength h in hookLengths) typeChanges.Add(RebarHookLengths.Describe(h, set.Tolerances.LengthMm));
            int typeChangesPending = hookLengths.Count(h => RebarHookLengths.NeedsChange(h, set.Tolerances.LengthMm));

            int buildable = bars.Count(b => b.Ok);
            int refusedRequired = bars.Count(b => !b.Ok && b.Rule != null && b.Rule.Required)
                                + covers.Count(c => !c.Ok && c.Rule != null && c.Rule.Required)
                                + systems.Count(x => !x.Ok && x.Required)
                                // A declared hook length is never optional: it names a type to change.
                                + hookLengths.Count(h => !h.Ok);
            int totalBars = bars.Where(b => b.Ok).Sum(b => b.Layout.Quantity);
            double totalSteelMm = bars.Where(b => b.Ok).Sum(b => b.ExpectedBarLengthMm * b.Layout.Quantity);

            var byCode = new JObject();
            foreach (var g in bars.Where(b => !b.Ok).GroupBy(b => b.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                byCode[g.Key] = g.Count();
            foreach (var g in covers.Where(c => !c.Ok).GroupBy(c => c.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                byCode[g.Key] = (byCode[g.Key] == null ? 0 : (int)byCode[g.Key]) + g.Count();
            foreach (var g in systems.Where(x => !x.Ok).GroupBy(x => x.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
                byCode[g.Key] = (byCode[g.Key] == null ? 0 : (int)byCode[g.Key]) + g.Count();
            foreach (var g in hookLengths.Where(x => !x.Ok).GroupBy(x => x.Code).OrderBy(g => g.Key, StringComparer.Ordinal))
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
                    ["mat_rules"] = set.MatRules.Count,
                    ["section_rules"] = set.SectionRules.Count,
                    ["wall_boundary_rules"] = set.WallBoundaryRules.Count,
                    ["area_rules"] = set.AreaRules.Count,
                    ["path_rules"] = set.PathRules.Count,
                    ["fabric_rules"] = set.FabricRules.Count
                },
                ["tolerances_mm"] = new JObject
                {
                    ["length"] = set.Tolerances.LengthMm,
                    ["spacing"] = set.Tolerances.SpacingMm,
                    ["cover"] = set.Tolerances.CoverMm
                },
                ["cover"] = coverRows,
                ["reinforcement"] = barRows,
                ["area"] = areaRows,
                ["path"] = pathRows,
                ["fabric"] = fabricRows,
                ["overlap_check"] = overlap.ToJson(),
                ["crossing_order"] = ReinforcementResolver.DescribeCrossing(crossing, bars),
                ["bar_type_changes"] = typeChanges,
                ["bar_type_changes_mean"] =
                    "every (bar type, hook type) pair whose hook length the set declares (hook_types[].lengths_mm): " +
                    "the length the model carries now, auto-calculated or fixed, the declared one, and whether the " +
                    "apply would write it (change). Each one changes a TYPE - every bar of that type with that hook, " +
                    "not only the bars this set builds. A pair already fixed at the declared length is listed with " +
                    "change: false.",
                ["summary"] = new JObject
                {
                    ["bar_type_changes"] = typeChangesPending,
                    ["rows"] = bars.Count + covers.Count + systems.Count,
                    ["system_rows_buildable"] = systemsBuildable,
                    ["system_rows_refused"] = systems.Count - systemsBuildable,
                    ["rebar_rows_buildable"] = buildable,
                    ["rebar_rows_refused"] = bars.Count - buildable,
                    ["cover_rows_to_set"] = covers.Count(c => c.Ok && !c.AlreadyRight),
                    ["cover_rows_already_right"] = covers.Count(c => c.Ok && c.AlreadyRight),
                    ["cover_rows_refused"] = covers.Count(c => !c.Ok),
                    ["required_rows_refused"] = refusedRequired,
                    ["expected_bars"] = totalBars,
                    ["expected_bars_exclude"] =
                        "area, path and fabric systems. Revit lays their bars and sheets from the declared numbers, " +
                        "and this plan does not predict how many; the apply verifies that at least one was produced.",
                    ["expected_steel_length_mm"] = Math.Round(totalSteelMm, 3),
                    ["expected_steel_length_means"] =
                        "declared centrelines multiplied by the bar count. It EXCLUDES hook length, which Revit " +
                        "adds itself, so the model will report more wherever a hook is declared. It is what the " +
                        "plan asked for, never a takeoff.",
                    ["refusals_by_code"] = byCode
                },
                ["would_apply"] = refusedRequired == 0 &&
                                  (buildable > 0 || systemsBuildable > 0 || typeChangesPending > 0 ||
                                   covers.Any(c => c.Ok && !c.AlreadyRight)),
                ["would_apply_means"] =
                    "true when nothing REQUIRED was refused and there is something to do. A rule marked " +
                    "required: false may be refused without stopping the rest - that is what the key is for.",
                ["refusal_codes"] = new JArray(ReinforcementResolver.AllCodes
                    .Concat(SystemReinforcementResolver.AllCodes).Distinct(StringComparer.Ordinal)),
                ["refusal_codes_mean"] =
                    "the CLOSED set of codes a refused row can carry, published here rather than described in " +
                    "the tool text - so it cannot drift from the code that emits it.",
                ["writes_nothing"] = true,
                ["chunk"] = chunk == null ? JValue.CreateNull() : (JToken)chunk.Chunking,
                ["next"] = "Send the same requirement_set to horizun_apply_reinforcement with dry_run=true to " +
                           "obtain a confirmation token, then again with dry_run=false and that token."
            };
            if (chunk != null)
                result["next"] = "This planned ONE piece of the set. Apply it with horizun_apply_reinforcement and the same " +
                                 "requirement_set and chunk - partition_sha256 included - rehearsing first, then for each " +
                                 "next_piece_index in turn (horizun_submit_job + horizun_job_status for long pieces). " +
                                 "The counts above are this piece's.";
            return CommandResult.Ok(result);
        }
    }
}
