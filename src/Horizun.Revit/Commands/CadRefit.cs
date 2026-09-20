// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// REFIT: a fitting whose runs change section, rebuilt at the new section - or not at all.
//
// MEASURED (campaign 6): writing a new width and height on both legs of an elbow left the
// elbow at the old size and Revit inserted a transition on each leg. That is what Revit does
// on its own; it is not what the drawing shows (one elbow at the new size). A refit replaces
// the fitting: it is deleted, the runs take their new section, and a fitting of the SAME kind
// is placed between the same runs - through the three typed commands that own those writes
// (horizun_delete_verified, horizun_write_params_verified, horizun_create_elements), each
// rehearsed and re-read by itself. This file adds the order, the viability test, the check
// afterwards, and the one thing none of them can do: undo all three together.
//
// A REFIT IS KEPT ONLY WHOLE. The three writes happen inside one transaction group. The group
// is kept only when the new fitting joins every run at the junction, has the new section at
// every end, and every run's OTHER connections are exactly what they were. Anything else - a
// step refused, a step failing, a check not met - rolls the group back and says which.
//
// NOT VIABLE, SAID BEFORE ANYTHING IS WRITTEN: a fitting joined to another fitting (Revit's
// transitions already there, a chain), a run of it not being resized, a round run, a kind this
// route does not place (a transition, a tap), or two runs asked for different sections.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class CadRefit
    {
        private sealed class Unit
        {
            public JObject Spec;
            public FamilyInstance Fitting;
            public string Part;
            public double WidthMm, HeightMm;
            public List<MEPCurve> Runs = new List<MEPCurve>();
            /// <summary>Per run: the neighbours of its OTHER connectors, which must survive.</summary>
            public Dictionary<long, List<long>> Protected = new Dictionary<long, List<long>>();
            public string NotViable;
            public JObject Before;
        }

        public static CommandResult Run(UIApplication app, Document doc, string title, JObject request,
                                        Func<string, ICommand> resolve)
        {
            bool dryRun = request.Value<bool?>("dry_run") ?? true;
            // Each unit is read JUST BEFORE it is carried out: an earlier refit in the same call may have
            // replaced the fitting at a run's other end, and that new fitting is what must be protected.
            var specs = (request["refit"] as JArray).OfType<JObject>().ToList();
            var rows = new JArray();
            RolledBackRehearsal rehearsal = null;
            if (dryRun)
            {
                rehearsal = new RolledBackRehearsal(doc, "Horizun: refit rehearsal");
                if (!rehearsal.Started) rehearsal = null;
            }
            int kept = 0, failed = 0, notViable = 0;
            try
            {
                foreach (JObject spec in specs)
                {
                    Unit u = Read(doc, spec);
                    JObject row = new JObject { ["fitting_id"] = u.Spec["fitting_id"], ["before"] = u.Before };
                    if (u.NotViable != null)
                    {
                        row["state"] = "not_viable"; row["why"] = u.NotViable; notViable++; rows.Add(row);
                        continue;
                    }
                    if (dryRun && rehearsal == null)
                    {
                        row["state"] = "not_rehearsed";
                        row["why"] = "the rehearsal group could not be opened, so the refit was not carried out to measure it";
                        failed++; rows.Add(row);
                        continue;
                    }
                    using (var group = new CheckedWriteGroup(doc, "Horizun: refit " + u.Spec["fitting_id"]))
                    {
                        string step;
                        JObject steps = new JObject();
                        string why = Carry(app, doc, title, u, resolve, request, steps, out step);
                        row["steps"] = steps;
                        JObject check = why == null ? Check(doc, u) : null;
                        if (check != null) row["check"] = check;
                        if (why == null && check.Value<bool>("holds"))
                        {
                            group.Keep();
                            row["state"] = dryRun ? "would_refit" : "refitted";
                            row["new_fitting_id"] = check["new_fitting_id"];
                            row["identity"] = "the fitting is replaced: " + u.Spec["fitting_id"] + " -> " + check["new_fitting_id"] +
                                              " (a new element id; the runs keep theirs)";
                            kept++;
                        }
                        else
                        {
                            group.Undo();
                            row["state"] = "rolled_back";
                            row["failed_at"] = why != null ? step : "check";
                            row["why"] = why ?? check.Value<string>("means");
                            row["rollback"] = group.Outcome;
                            row["rollback_status"] = group.RollbackStatus;
                            failed++;
                        }
                        row["group"] = group.Outcome;
                    }
                    rows.Add(row);
                }
            }
            finally { rehearsal?.Dispose(); }

            var result = new JObject
            {
                ["document"] = title,
                ["dry_run"] = dryRun,
                ["refits"] = rows,
                ["refitted"] = dryRun ? 0 : kept,
                ["would_refit"] = dryRun ? kept : 0,
                ["rolled_back"] = failed,
                ["not_viable"] = notViable,
                ["rehearsal_rollback"] = rehearsal == null ? null : new JObject
                {
                    ["status"] = rehearsal.RollbackStatus, ["confirmed"] = rehearsal.RollbackConfirmed
                },
                ["state"] = failed == 0 && notViable == 0 ? (dryRun ? "rehearsed" : "applied") : "partial",
                ["means"] = "each refit deletes the fitting, writes the runs' new section and places a fitting of the same " +
                            "kind between the same runs - each step by its own typed command - inside one group kept only " +
                            "when the new fitting joins every run with the new section at every end and every run's other " +
                            "connections are unchanged; otherwise the group is rolled back and the step that failed is named."
            };
            // A refit that did not happen whole is not a success, and a composing caller (the update's
            // apply) must see a refusal - its rehearsal then stops before anything is written.
            if (failed > 0 || notViable > 0)
                return CommandResult.FailWithDetail("refit: " + failed + " rolled back, " + notViable + " not viable. " +
                    "Every refit that was not kept whole was undone.", result);
            return CommandResult.Ok(result);
        }

        private static Unit Read(Document doc, JObject spec)
        {
            var u = new Unit { Spec = spec };
            long? fid = spec.Value<long?>("fitting_id");
            u.WidthMm = spec.Value<double?>("width_mm") ?? 0;
            u.HeightMm = spec.Value<double?>("height_mm") ?? 0;
            u.Fitting = fid.HasValue ? doc.GetElement(Rid.Make(fid.Value)) as FamilyInstance : null;
            if (u.Fitting?.MEPModel?.ConnectorManager == null) { u.NotViable = "fitting " + fid + " is not an MEP fitting in this model"; return u; }
            if (u.WidthMm <= 0 || u.HeightMm <= 0) { u.NotViable = "width_mm and height_mm must be positive"; return u; }
            u.Part = PartOf(u.Fitting);
            var before = new JObject { ["part"] = u.Part, ["connector_sizes_mm"] = new JArray(), ["runs"] = new JArray() };
            u.Before = before;
            if (u.Part != "Elbow" && u.Part != "Tee" && u.Part != "Cross")
            { u.NotViable = "a " + u.Part + " is not rebuilt by this route (elbow, tee and cross only)"; return u; }
            var asked = new HashSet<long>((spec["runs"] as JArray ?? new JArray()).Select(t => (long)t));
            foreach (Connector c in MepFacts.Ordered(u.Fitting.MEPModel.ConnectorManager))
            {
                ((JArray)before["connector_sizes_mm"]).Add(Size(c));
                foreach (Connector o in c.AllRefs)
                {
                    if (o.Owner == null || o.Owner.Id == u.Fitting.Id) continue;
                    var run = o.Owner as MEPCurve;
                    if (run == null)
                    { u.NotViable = "the " + u.Part + " is joined to " + Rid.Value(o.Owner.Id) + ", which is not a run (a fitting chain is left as it is)"; return u; }
                    if (u.Runs.Any(x => x.Id == run.Id)) continue;
                    u.Runs.Add(run);
                }
            }
            foreach (MEPCurve run in u.Runs)
            {
                ((JArray)before["runs"]).Add(new JObject { ["element_id"] = Rid.Value(run.Id), ["section_mm"] = SectionOf(run) });
                if (!asked.Contains(Rid.Value(run.Id)))
                { u.NotViable = "run " + Rid.Value(run.Id) + " of this " + u.Part + " is not being resized - it is protected, and a refit would change its connection"; return u; }
                if (run.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM) == null || !run.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).HasValue)
                { u.NotViable = "run " + Rid.Value(run.Id) + " is not rectangular"; return u; }
                var others = new List<long>();
                foreach (Connector c in MepFacts.Ordered(run.ConnectorManager))
                    foreach (Connector o in c.AllRefs)
                        if (o.Owner != null && o.Owner.Id != run.Id && o.Owner.Id != u.Fitting.Id) others.Add(Rid.Value(o.Owner.Id));
                u.Protected[Rid.Value(run.Id)] = others.Distinct().ToList();
            }
            if (asked.Count != u.Runs.Count)
                u.NotViable = "the refit names " + asked.Count + " run(s) and the " + u.Part + " joins " + u.Runs.Count;
            return u;
        }

        /// <summary>The three delegated writes, in order. Returns null when all three were verified by their commands.</summary>
        private static string Carry(UIApplication app, Document doc, string title, Unit u, Func<string, ICommand> resolve,
                                    JObject request, JObject steps, out string step)
        {
            var runIds = u.Runs.Select(r => Rid.Value(r.Id)).ToList();
            string kind = u.Part.ToLowerInvariant();
            // WHERE THE FITTING STOOD - where its runs must meet again. Read before it is deleted.
            XYZ at = (u.Fitting.Location as LocationPoint)?.Point;
            if (at == null) { step = "read"; return "the fitting has no insertion point to rebuild at"; }
            step = "delete";
            JObject del = Call(app, resolve, "horizun_delete_verified",
                new JObject { ["target_document"] = title, ["mode"] = "ids", ["ids"] = new JArray(Rid.Value(u.Fitting.Id)) }, steps, step);
            if (del == null) return steps[step]?.Value<string>("error") ?? "the delete was refused";
            step = "resize";
            var writes = new JArray();
            foreach (long id in runIds)
            {
                writes.Add(new JObject { ["target_id"] = id, ["parameter"] = "RBS_CURVE_WIDTH_PARAM", ["value"] = u.WidthMm / 304.8 });
                writes.Add(new JObject { ["target_id"] = id, ["parameter"] = "RBS_CURVE_HEIGHT_PARAM", ["value"] = u.HeightMm / 304.8 });
            }
            JObject wr = Call(app, resolve, "horizun_write_params_verified",
                new JObject { ["target_document"] = title, ["writes"] = writes }, steps, step);
            if (wr == null) return steps[step]?.Value<string>("error") ?? "the resize was refused";
            // THE RUNS MEET WHERE THE FITTING STOOD. MEASURED (campaign 7): with the elbow deleted, its two runs
            // end 431 mm apart (the elbow had cut them back), and a fitting joins connectors that meet.
            step = "meet";
            ICommand mover = resolve?.Invoke("horizun_transform_elements");
            if (mover == null) return "horizun_transform_elements could not be resolved";
            var met = new JArray();
            foreach (MEPCurve run in u.Runs)
            {
                var lc = run.Location as LocationCurve;
                if (lc?.Curve == null) return "run " + Rid.Value(run.Id) + " has no location line";
                XYZ p0 = lc.Curve.GetEndPoint(0), p1 = lc.Curve.GetEndPoint(1);
                bool zeroNear = new XYZ(p0.X - at.X, p0.Y - at.Y, 0).GetLength() <= new XYZ(p1.X - at.X, p1.Y - at.Y, 0).GetLength();
                XYZ keep = zeroNear ? p1 : p0, near = zeroNear ? p0 : p1;
                var args = new JObject
                {
                    ["target_document"] = title, ["units"] = "mm",
                    ["operations"] = new JArray(new JObject
                    {
                        ["operation"] = "set_curve", ["element_ids"] = new JArray(Rid.Value(run.Id)),
                        ["start"] = new JArray(keep.X * 304.8, keep.Y * 304.8, keep.Z * 304.8),
                        ["end"] = new JArray(at.X * 304.8, at.Y * 304.8, near.Z * 304.8)
                    })
                };
                var sub = new JObject();
                if (Call(app, resolve, "horizun_transform_elements", args, sub, "meet") == null)
                {
                    steps["meet"] = sub["meet"];
                    return "run " + Rid.Value(run.Id) + " could not be brought to where the fitting stood: " +
                           (sub["meet"]?.Value<string>("error") ?? "refused");
                }
                met.Add(new JObject { ["element_id"] = Rid.Value(run.Id), ["end_moved_mm"] = Math.Round(near.DistanceTo(new XYZ(at.X, at.Y, near.Z)) * 304.8, 1) });
            }
            steps["meet"] = new JObject { ["ok"] = true, ["runs"] = met, ["at_mm"] = new JArray(Math.Round(at.X * 304.8, 1), Math.Round(at.Y * 304.8, 1)) };
            step = "place";
            // A FAULT SEAM FOR TESTS, off unless Revit was started with it: after the delete, the resize and the
            // move have been written, the placement is reported failed - to measure what the group undoes.
            // "cad-refit-place" fires in a rehearsal and an apply; "cad-refit-place-apply" in an apply only.
            bool applying = request.Value<bool?>("dry_run") == false;
            if (FaultInjected("cad-refit-place") || (applying && FaultInjected("cad-refit-place-apply")))
            {
                steps[step] = new JObject { ["ok"] = false, ["error"] = "fault_injected: HORIZUN_TEST_FAIL_ACTION names the refit placement" };
                return "fault_injected before the new fitting was placed (delete, resize and move were already written)";
            }
            JObject pl = Call(app, resolve, "horizun_create_elements", new JObject
            {
                ["target_document"] = title,
                ["elements"] = new JArray(new JObject
                {
                    ["kind"] = "fitting", ["fitting"] = kind,
                    ["elements"] = new JArray(runIds.Select(id => (JToken)new JObject { ["element_id"] = id }))
                })
            }, steps, step);
            if (pl == null) return steps[step]?.Value<string>("error") ?? "the new fitting was refused";
            return null;
        }

        /// <summary>Rehearse, then apply with the token the rehearsal issued - the children's own protocol.</summary>
        private static JObject Call(UIApplication app, Func<string, ICommand> resolve, string tool, JObject args, JObject steps, string step)
        {
            ICommand child = resolve?.Invoke(tool);
            if (child == null) { steps[step] = new JObject { ["ok"] = false, ["error"] = tool + " could not be resolved" }; return null; }
            args["dry_run"] = true;
            args["idempotency_key"] = "cad-refit-" + step + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            CommandResult dry = child.Execute(app, args.ToString(Formatting.None));
            string token = (dry.Data as JObject)?.Value<string>("confirmation_token");
            if (!dry.Success)
            { steps[step] = new JObject { ["ok"] = false, ["tool"] = tool, ["error"] = dry.Error }; return null; }
            args["dry_run"] = false;
            if (token != null) args["confirmation_token"] = token;
            args["idempotency_key"] = "cad-refit-" + step + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            int mark = Interference.Current?.Seen.Count ?? 0;
            CommandResult done = child.Execute(app, args.ToString(Formatting.None));
            steps[step] = new JObject { ["ok"] = done.Success, ["tool"] = tool, ["error"] = done.Success ? null : done.Error };
            // what Revit raised while THIS step ran - its own words, the reason a step failed
            Interference watch = Interference.Current;
            if (watch != null && watch.Seen.Count > mark) steps[step]["revit_raised"] = RaisedRecord.Window(watch.Seen, mark);
            return done.Success ? (done.Data as JObject ?? new JObject()) : null;
        }

        /// <summary>After the writes: one new fitting of the same kind joins every run, at the new section, nothing else moved.</summary>
        private static JObject Check(Document doc, Unit u)
        {
            var o = new JObject();
            var problems = new List<string>();
            FamilyInstance fresh = null;
            foreach (MEPCurve run in u.Runs)
            {
                string sec = SectionOf(run);
                string want = Fmt(u.WidthMm) + "x" + Fmt(u.HeightMm);
                if (sec != want) problems.Add("run " + Rid.Value(run.Id) + " is " + sec + ", not " + want);
                var neighbours = new List<Element>();
                foreach (Connector c in MepFacts.Ordered(run.ConnectorManager))
                    foreach (Connector x in c.AllRefs)
                        if (x.Owner != null && x.Owner.Id != run.Id) neighbours.Add(x.Owner);
                var fittings = neighbours.OfType<FamilyInstance>().Where(f => !u.Protected[Rid.Value(run.Id)].Contains(Rid.Value(f.Id))).ToList();
                if (fittings.Count != 1) { problems.Add("run " + Rid.Value(run.Id) + " is joined at the junction to " + fittings.Count + " new element(s), not one"); continue; }
                if (fresh == null) fresh = fittings[0];
                else if (fresh.Id != fittings[0].Id) problems.Add("the runs are joined to different new fittings");
                var still = neighbours.Select(n => Rid.Value(n.Id)).ToList();
                foreach (long p in u.Protected[Rid.Value(run.Id)])
                    if (!still.Contains(p)) problems.Add("run " + Rid.Value(run.Id) + " lost its connection to " + p);
            }
            if (fresh != null)
            {
                o["new_fitting_id"] = Rid.Value(fresh.Id);
                o["new_fitting_part"] = PartOf(fresh);
                if (PartOf(fresh) != u.Part) problems.Add("the new fitting is a " + PartOf(fresh) + ", not a " + u.Part);
                var sizes = new JArray();
                foreach (Connector c in MepFacts.Ordered(fresh.MEPModel.ConnectorManager))
                {
                    sizes.Add(Size(c));
                    if (Size(c) != Fmt(u.WidthMm) + "x" + Fmt(u.HeightMm)) problems.Add("the new fitting has an end at " + Size(c));
                }
                o["new_fitting_connector_sizes_mm"] = sizes;
            }
            else problems.Add("no new fitting joins the runs");
            o["holds"] = problems.Count == 0;
            o["problems"] = new JArray(problems);
            o["means"] = problems.Count == 0
                ? "one new " + u.Part.ToLowerInvariant() + " joins every run at the new section; every other connection is as it was"
                : string.Join("; ", problems);
            return o;
        }

        private static bool FaultInjected(string key)
        {
            string named = Environment.GetEnvironmentVariable("HORIZUN_TEST_FAIL_ACTION");
            return !string.IsNullOrWhiteSpace(named) && string.Equals(named.Trim(), key, StringComparison.Ordinal);
        }

        private static string PartOf(FamilyInstance f)
        {
            try { return ((PartType)(f.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() ?? -1)).ToString(); }
            catch { return ""; }
        }

        private static string Fmt(double mm) => Math.Round(mm, 1).ToString(CultureInfo.InvariantCulture);

        private static string Size(Connector c) => c.Shape == ConnectorProfileType.Round
            ? "D" + Fmt(c.Radius * 2 * 304.8) : Fmt(c.Width * 304.8) + "x" + Fmt(c.Height * 304.8);

        private static string SectionOf(MEPCurve run)
        {
            Parameter w = run.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            Parameter h = run.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            if (w == null || h == null || !w.HasValue || !h.HasValue) return null;
            return Fmt(w.AsDouble() * 304.8) + "x" + Fmt(h.AsDouble() * 304.8);
        }
    }
}
