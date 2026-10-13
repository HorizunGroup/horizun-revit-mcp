// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_query_structure, the two L10 modes (acero 3.0).
// Original Horizun code. Read-only: no transaction is opened.
//
// mode=parse_notation  the bar notation of a detail schedule, line by line, read by
//                      RebarNotation (Core) into millimetres or refused with a code. It
//                      reads TEXT, not the model, and needs no document.
// mode=compare_bars    two sets of bars from the active model, compared BAR BY BAR by
//                      RebarBarComparison (Core) against tolerances the caller declares.
//
// A BAR, here, is one position of a set that has a bar: position 0's centreline as Revit
// draws it (hooks and bends included, tessellated - RebarFacts.CentrelinePointsMm, the one
// place that is computed) offset by the position's GetBarPositionTransform. That is how the
// apply and the audit already place every bar of a set. A position Revit says carries no
// bar (a suppressed first or last bar) is not a bar. A free-form set with more than one
// position is not placed this way, and its bars are reported unreadable rather than guessed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryStructureCommand
    {
        private const int MaxSetsPerSide = 2000;

        private CommandResult ParseNotation(JObject request)
        {
            string code, error;
            JObject result = RebarNotation.ParseLines(request["lines"], out code, out error);
            if (result == null) return CommandResult.Fail(code + ": " + error);
            var o = new JObject { ["mode"] = "parse_notation" };
            foreach (JProperty p in result.Properties()) o[p.Name] = p.Value;
            o["means"] = "each line as a schedule writes it, read without guessing: bars (count, system, size), a length " +
                         "and a spacing or stirrup zones in millimetres, or the code it was refused with. Nothing in the " +
                         "model was read or changed.";
            return CommandResult.Ok(o);
        }

        private CommandResult CompareBars(Document doc, JObject request, int offset, int maxRows)
        {
            string code, error;
            RebarBarComparison.Tolerances tol = RebarBarComparison.ReadTolerances(request["bar_tolerances"], out code, out error);
            if (tol == null) return CommandResult.Fail(code + ": " + error);

            List<Rebar> setsA, setsB;
            CommandResult bad = Side(doc, request, "a", out setsA);
            if (bad != null) return bad;
            bad = Side(doc, request, "b", out setsB);
            if (bad != null) return bad;

            JArray rowsA = BarRows(doc, setsA), rowsB = BarRows(doc, setsB);
            List<RebarBarComparison.Bar> a = RebarBarComparison.ReadBars(rowsA, "a", out code, out error);
            if (a == null) return CommandResult.Fail(code + ": " + error);
            List<RebarBarComparison.Bar> b = RebarBarComparison.ReadBars(rowsB, "b", out code, out error);
            if (b == null) return CommandResult.Fail(code + ": " + error);

            JObject result = RebarBarComparison.Compare(a, b, tol, offset, maxRows);
            var o = new JObject { ["mode"] = "compare_bars" };
            foreach (JProperty p in result.Properties()) o[p.Name] = p.Value;
            o["scope"] = new JObject
            {
                ["sets_a"] = new JArray(setsA.Select(x => (object)Rid.Value(x.Id)).ToArray()),
                ["sets_b"] = new JArray(setsB.Select(x => (object)Rid.Value(x.Id)).ToArray()),
                ["bar_means"] =
                    "one position of a set that carries a bar: position 0's centreline as Revit draws it, offset by " +
                    "that position's GetBarPositionTransform - how the apply and the audit place every bar. A position " +
                    "Revit says has no bar is not counted."
            };
            o["writes_nothing"] = true;
            return CommandResult.Ok(o);
        }

        /// <summary>
        /// One side's bar sets: element_ids_&lt;side&gt; (every id must be a rebar set) or
        /// host_id_&lt;side&gt; (every set hosted there, at least one). Exactly one of the two.
        /// </summary>
        private static CommandResult Side(Document doc, JObject request, string side, out List<Rebar> sets)
        {
            sets = new List<Rebar>();
            var ids = request["element_ids_" + side] as JArray;
            JToken host = request["host_id_" + side];
            bool hasIds = ids != null, hasHost = host != null && host.Type != JTokenType.Null;
            if (hasIds == hasHost)
                return CommandResult.Fail("compare_bars needs exactly one of element_ids_" + side + " (rebar set ids) or host_id_" +
                                          side + " (every set in that host) for side " + side + ". Nothing was compared.");
            if (hasIds)
            {
                if (ids.Count == 0 || ids.Count > MaxSetsPerSide)
                    return CommandResult.Fail("element_ids_" + side + " must list 1 to " + MaxSetsPerSide + " rebar sets. Nothing was compared.");
                var seen = new HashSet<long>();
                for (int i = 0; i < ids.Count; i++)
                {
                    long v = ids[i].Type == JTokenType.Integer ? (long)ids[i] : -1;
                    if (!Rid.CanRepresent(v)) return CommandResult.Fail("element_ids_" + side + "[" + i + "] is not an ElementId. Nothing was compared.");
                    if (!seen.Add(v)) continue;
                    var bar = doc.GetElement(Rid.Make(v)) as Rebar;
                    if (bar == null)
                        return CommandResult.Fail("element_ids_" + side + "[" + i + "] = " + v + " is not a rebar set in this document. " +
                                                  "A comparison that skipped it would report its bars as missing. Nothing was compared.");
                    sets.Add(bar);
                }
            }
            else
            {
                long hid = host.Type == JTokenType.Integer ? (long)host : -1;
                if (!Rid.CanRepresent(hid) || hid < 0) return CommandResult.Fail("host_id_" + side + " is not an ElementId. Nothing was compared.");
                ElementId want = Rid.Make(hid);
                if (doc.GetElement(want) == null)
                    return CommandResult.Fail("host_id_" + side + " = " + hid + " names no element in this document. Nothing was compared.");
                sets = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>()
                    .Where(r => { try { return r.GetHostId() == want; } catch { return false; } }).ToList();
                if (sets.Count == 0)
                    return CommandResult.Fail("host_id_" + side + " = " + hid + " hosts no rebar set. An empty side is not a side to " +
                                              "compare with. Nothing was compared.");
            }
            sets = sets.OrderBy(x => Rid.Value(x.Id)).ToList();
            return null;
        }

        /// <summary>One row per bar, in the shape RebarBarComparison.ReadBars reads.</summary>
        private static JArray BarRows(Document doc, List<Rebar> sets)
        {
            var rows = new JArray();
            foreach (Rebar set in sets)
            {
                long sid = Rid.Value(set.Id);
                var bt = doc.GetElement(set.GetTypeId()) as RebarBarType;
                string typeName = bt == null ? null : Str(() => bt.Name);
                double? dia = bt == null ? null : Num(() => bt.BarNominalDiameter);
                JToken diaMm = dia.HasValue ? (JToken)Math.Round(dia.Value * FtToMm, 3) : JValue.CreateNull();
                List<double[]> first = RebarFacts.CentrelinePointsMm(set, false);

                int? slots = null;
                try { slots = set.NumberOfBarPositions; } catch { }
                RebarShapeDrivenAccessor acc = null;
                try { acc = set.GetShapeDrivenAccessor(); } catch { }

                if (!slots.HasValue || slots.Value < 1)
                {
                    rows.Add(new JObject
                    {
                        ["id"] = sid + ":?", ["set_id"] = sid, ["position"] = null, ["bar_type"] = typeName,
                        ["nominal_diameter_mm"] = diaMm, ["centreline_points_mm"] = null,
                        ["why"] = "the set would not say how many bar positions it has, so its bars were not compared."
                    });
                    continue;
                }
                for (int i = 0; i < slots.Value; i++)
                {
                    bool? exists = null;
                    try { exists = set.DoesBarExistAtPosition(i); } catch { }
                    if (exists == false) continue;
                    var row = new JObject
                    {
                        ["id"] = sid + ":" + i, ["set_id"] = sid, ["position"] = i, ["bar_type"] = typeName,
                        ["nominal_diameter_mm"] = diaMm
                    };
                    string why = null;
                    double[] offset = null;
                    if (exists == null) why = "whether a bar exists at this position could not be read.";
                    else if (first == null) why = "the set would not return its centreline, so this bar's shape is unknown.";
                    else if (acc == null && slots.Value > 1)
                        why = "the set is not shape-driven, and the place of each of its bars is not read by this bridge.";
                    else if (acc == null) offset = new[] { 0.0, 0.0, 0.0 };
                    else
                    {
                        Transform t = null;
                        try { t = acc.GetBarPositionTransform(i); } catch { }
                        if (t == null) why = "the transform of this bar position could not be read.";
                        else offset = new[] { t.Origin.X * FtToMm, t.Origin.Y * FtToMm, t.Origin.Z * FtToMm };
                    }
                    if (why != null)
                    {
                        row["centreline_points_mm"] = null;
                        row["why"] = why;
                    }
                    else
                    {
                        var pts = new JArray();
                        foreach (double[] p in first)
                            pts.Add(new JArray(Math.Round(p[0] + offset[0], 3), Math.Round(p[1] + offset[1], 3), Math.Round(p[2] + offset[2], 3)));
                        row["centreline_points_mm"] = pts;
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }
    }
}
