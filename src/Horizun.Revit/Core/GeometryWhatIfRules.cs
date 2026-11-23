// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_measure_geometry what_if, and of
// horizun_split_by_level split_at=openings.
//
// Field data (2026-10, 591 execute_python purposes): the second most common reason to
// write Python was a rehearsal that undoes itself - "simulate deleting / joining this,
// measure, take it back" - and those scripts failed on the API's own rules (a
// transaction left open, an object used after its deletion). what_if is that
// rehearsal as a typed operation: apply inside a group, measure, ALWAYS roll back, and
// then prove the rollback by measuring again and comparing with the first reading.
//
// This file holds what can be proven without Revit:
//   - the what_if request (action, ids, move vector in the caller's unit);
//   - the comparison of two measure readings within a tolerance (the rollback proof);
//   - the plan of pieces for splitting a straight wall at the jambs of its openings.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class GeometryWhatIfRules
    {
        public const int MaxIds = 100;
        /// <summary>1 km: a move further than this is a typo, not a rehearsal.</summary>
        public const double MaxMoveFeet = 1000.0 / 0.3048;

        public static bool TryParse(JToken token, double lengthFromFeet, out WhatIfSpec spec, out string error)
        {
            spec = null; error = null;
            var o = token as JObject;
            if (o == null) { error = "what_if must be an object {action, ids[, vector]}."; return false; }
            foreach (JProperty p in o.Properties())
                if (p.Name != "action" && p.Name != "ids" && p.Name != "vector")
                { error = "what_if." + p.Name + " is not an argument; what_if takes action, ids and (move) vector."; return false; }
            var s = new WhatIfSpec { Action = ((string)o["action"] ?? "").Trim().ToLowerInvariant() };
            if (Array.IndexOf(WhatIfActions.All, s.Action) < 0)
            { error = "what_if.action must be one of " + string.Join(", ", WhatIfActions.All) + "."; return false; }
            var ids = o["ids"] as JArray;
            if (ids == null || ids.Count == 0) { error = "what_if.ids is required: the elements the rehearsal acts on."; return false; }
            foreach (JToken t in ids)
            {
                if (t.Type != JTokenType.Integer) { error = "what_if.ids contains a value that is not an integer element id: " + t; return false; }
                long id = (long)t;
                if (!s.Ids.Contains(id)) s.Ids.Add(id);
            }
            if (s.Ids.Count > MaxIds) { error = "what_if.ids carries " + s.Ids.Count + " ids; the limit is " + MaxIds + "."; return false; }
            if ((s.Action == WhatIfActions.Join || s.Action == WhatIfActions.Unjoin) && s.Ids.Count != 2)
            { error = "what_if " + s.Action + " takes exactly two distinct ids: the pair."; return false; }
            JToken vec = o["vector"];
            if (s.Action == WhatIfActions.Move)
            {
                var v = vec as JArray;
                if (v == null || v.Count != 3 || v.Any(x => x.Type != JTokenType.Integer && x.Type != JTokenType.Float))
                { error = "what_if move needs vector: [x, y, z] in the request unit."; return false; }
                if (!(lengthFromFeet > 0)) { error = "unit must be mm, m or feet."; return false; }
                s.VectorFeet = v.Select(x => (double)x / lengthFromFeet).ToArray();
                double len = Math.Sqrt(s.VectorFeet.Sum(x => x * x));
                if (len < 1e-9) { error = "what_if move vector is zero: nothing would move."; return false; }
                if (len > MaxMoveFeet) { error = "what_if move vector is longer than 1 km; refused as a likely unit mistake."; return false; }
            }
            else if (vec != null && vec.Type != JTokenType.Null)
            { error = "what_if.vector is only for action=move."; return false; }
            spec = s;
            return true;
        }

        /// <summary>
        /// horizun_verify_changes operation=what_if: the rehearsal (what_if), what to measure
        /// (measures, default bounding_box + solid_volume) and on which elements (element_ids,
        /// default the what_if ids), in which unit.
        /// </summary>
        public static bool TryParseRequest(JObject request, out MeasureRequest measure, out WhatIfSpec spec, out string error)
        {
            measure = null; spec = null; error = null;
            double fromFeet;
            if (!GeometryMeasureRules.TryUnit((string)request["unit"] ?? "mm", out fromFeet)) { error = "unit must be mm, m or feet."; return false; }
            JToken w = request["what_if"];
            if (w == null || w.Type == JTokenType.Null) { error = "operation=what_if needs what_if: {action, ids[, vector]}."; return false; }
            if (!TryParse(w, fromFeet, out spec, out error)) return false;
            JToken ids = request["element_ids"];
            var shaped = new JObject
            {
                ["ids"] = ids == null || ids.Type == JTokenType.Null ? new JArray(spec.Ids) : ids.DeepClone(),
                ["measures"] = request["measures"]?.DeepClone() ?? new JArray(MeasureKinds.BoundingBox, MeasureKinds.SolidVolume),
                ["unit"] = (string)request["unit"] ?? "mm"
            };
            if (!GeometryMeasureRules.TryParse(shaped, out measure, out error)) { spec = null; return false; }
            return true;
        }

        /// <summary>
        /// The paths where two readings of the same measures differ by more than
        /// <paramref name="tolerance"/> (numbers) or at all (anything else). Empty: equal.
        /// Used to prove a rollback: the reading after it must equal the reading before.
        /// </summary>
        public static List<string> Differences(JToken before, JToken after, double tolerance, int max = 20)
        {
            var found = new List<string>();
            Walk(before, after, "", tolerance, found, max);
            return found;
        }

        private static void Walk(JToken a, JToken b, string path, double tol, List<string> found, int max)
        {
            if (found.Count >= max) return;
            bool aNull = a == null || a.Type == JTokenType.Null, bNull = b == null || b.Type == JTokenType.Null;
            if (aNull || bNull) { if (aNull != bNull) found.Add(path + (aNull ? " appeared" : " vanished")); return; }
            bool aNum = a.Type == JTokenType.Integer || a.Type == JTokenType.Float;
            bool bNum = b.Type == JTokenType.Integer || b.Type == JTokenType.Float;
            if (aNum && bNum)
            {
                double x = (double)a, y = (double)b;
                if (Math.Abs(x - y) > tol)
                    found.Add(path + ": " + x.ToString("R", CultureInfo.InvariantCulture) + " -> " + y.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (a is JObject oa && b is JObject ob)
            {
                foreach (string k in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
                    Walk(oa[k], ob[k], path.Length == 0 ? k : path + "." + k, tol, found, max);
                return;
            }
            if (a is JArray la && b is JArray lb)
            {
                if (la.Count != lb.Count) { found.Add(path + ": " + la.Count + " items -> " + lb.Count); return; }
                for (int i = 0; i < la.Count; i++) Walk(la[i], lb[i], path + "[" + i + "]", tol, found, max);
                return;
            }
            if (!JToken.DeepEquals(a, b)) found.Add(path + ": changed");
        }
    }

    // ---- split a straight wall at the jambs of its openings ----

    public sealed class OpeningSpan
    {
        public long Id;
        /// <summary>Distance of the opening's centre from the wall's start, along its axis.</summary>
        public double Center;
        /// <summary>The opening's width along the axis (rough width when the family has one).</summary>
        public double Width;
    }

    public sealed class WallPiece
    {
        public double Start, End;
        public bool IsOpening;
        public List<long> OpeningIds = new List<long>();
        public double Length => End - Start;
    }

    public sealed class OpeningSplitPlan
    {
        public List<WallPiece> Pieces = new List<WallPiece>();
        public string Refusal;
        public List<string> Notes = new List<string>();
        public bool Ok => Refusal == null;
    }

    public static class OpeningSplitRules
    {
        /// <summary>No piece shorter than this is created (50 mm); a shorter side gap is absorbed by its opening piece.</summary>
        public const double MinPieceFeet = 50.0 / 304.8;
        /// <summary>An opening may run past a wall end by at most this (1 mm) before the plan refuses.</summary>
        public const double EndToleranceFeet = 1.0 / 304.8;

        /// <summary>
        /// Pieces along a straight wall of <paramref name="length"/>: a side piece between
        /// openings and an opening piece over each opening's span (jamb to jamb). Openings
        /// whose spans overlap or leave less than MinPieceFeet between them share one
        /// opening piece; a side gap shorter than MinPieceFeet at a wall end is absorbed by
        /// the neighbouring opening piece. Units are the caller's (feet in Revit).
        /// </summary>
        public static OpeningSplitPlan Plan(double length, IList<OpeningSpan> openings, double minPiece = MinPieceFeet)
        {
            var plan = new OpeningSplitPlan();
            if (!(length > 0)) { plan.Refusal = "the wall has no measurable length"; return plan; }
            if (openings == null || openings.Count == 0) { plan.Refusal = "the wall hosts no door or window: nothing to split at"; return plan; }
            var spans = new List<WallPiece>();
            foreach (OpeningSpan o in openings.OrderBy(x => x.Center))
            {
                if (!(o.Width > 0)) { plan.Refusal = "opening " + o.Id + " has no readable width"; return plan; }
                double a = o.Center - o.Width / 2, b = o.Center + o.Width / 2;
                if (a < -EndToleranceFeet || b > length + EndToleranceFeet)
                { plan.Refusal = "opening " + o.Id + " runs past the end of the wall"; return plan; }
                a = Math.Max(0, a); b = Math.Min(length, b);
                WallPiece last = spans.LastOrDefault();
                if (last != null && a - last.End < minPiece)
                {
                    if (a - last.End > 0) plan.Notes.Add("openings " + last.OpeningIds.Last() + " and " + o.Id + " are less than " +
                        (int)Math.Round(minPiece * 304.8) + " mm apart and share one opening piece");
                    last.End = Math.Max(last.End, b);
                    last.OpeningIds.Add(o.Id);
                    continue;
                }
                var span = new WallPiece { Start = a, End = b, IsOpening = true };
                span.OpeningIds.Add(o.Id);
                spans.Add(span);
            }
            if (spans[0].Start < minPiece)
            {
                if (spans[0].Start > 0) plan.Notes.Add("the side before opening " + spans[0].OpeningIds[0] + " is shorter than " + (int)Math.Round(minPiece * 304.8) + " mm and stays in its opening piece");
                spans[0].Start = 0;
            }
            WallPiece tail = spans[spans.Count - 1];
            if (length - tail.End < minPiece)
            {
                if (length - tail.End > 0) plan.Notes.Add("the side after opening " + tail.OpeningIds.Last() + " is shorter than " + (int)Math.Round(minPiece * 304.8) + " mm and stays in its opening piece");
                tail.End = length;
            }
            double cursor = 0;
            foreach (WallPiece s in spans)
            {
                if (s.Start > cursor) plan.Pieces.Add(new WallPiece { Start = cursor, End = s.Start });
                plan.Pieces.Add(s);
                cursor = s.End;
            }
            if (cursor < length) plan.Pieces.Add(new WallPiece { Start = cursor, End = length });
            if (plan.Pieces.Count < 2) { plan.Refusal = "the opening spans the whole wall: there is nothing to split off"; plan.Pieces.Clear(); }
            return plan;
        }

        /// <summary>The index of the piece whose span contains <paramref name="at"/>, or -1.</summary>
        public static int PieceAt(IList<WallPiece> pieces, double at, double tol = EndToleranceFeet)
        {
            for (int i = 0; i < pieces.Count; i++)
                if (at >= pieces[i].Start - tol && at <= pieces[i].End + tol) return i;
            return -1;
        }
    }
}
