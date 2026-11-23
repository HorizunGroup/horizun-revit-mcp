// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_selection_context (read-only).
//
// Field data (October, 763 execute_python samples): about sixty scripts from four
// people open the same way - "read, without modifying, the current selection and its
// neighbours" - and a second family photographs a set of elements before and after a
// step ("foto/huella") to see what the step really moved. Both were rewritten by hand
// in every session. This file holds what can be proved without Revit:
//
//  - the request: operation context | snapshot | diff, the source of elements (ids,
//    selector or the current selection), which sections, the radius, the parameters;
//  - which faces are the "main" ones (largest area first);
//  - how a neighbour relates to the element (joined, cuts it, is cut by it);
//  - the element fingerprint a snapshot stores, and the comparison of two of them,
//    with a stated tolerance: a value that could not be read is "unreadable", never
//    "unchanged";
//  - the per-document store of named snapshots, bounded.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SelectionContextOps
    {
        public const string Context = "context";
        public const string Snapshot = "snapshot";
        public const string Diff = "diff";
        /// <summary>Do these ids / unique_ids still name what the caller read? (ElementIdentityGuard.Validate)</summary>
        public const string ValidateIds = "validate_ids";
        /// <summary>What does this view draw, per category, with group members and linked elements (and, optionally, only those in a room or region).</summary>
        public const string ViewContents = "view_contents";
        public static readonly string[] All = { Context, Snapshot, Diff, ValidateIds, ViewContents };
    }

    public static class SelectionContextSections
    {
        public const string Geometry = "geometry";
        public const string Relations = "relations";
        public const string Neighbors = "neighbors";
        public const string Parameters = "parameters";
        public static readonly string[] All = { Geometry, Relations, Neighbors, Parameters };
    }

    public sealed class SelectionContextRequest
    {
        public string Operation = SelectionContextOps.Context;
        public List<long> Ids = new List<long>();
        public SelectorSpec Selector;
        /// <summary>The current selection. Implied for context when neither ids nor selector is sent.</summary>
        public bool Selection;
        public HashSet<string> Sections = new HashSet<string>(SelectionContextSections.All, StringComparer.Ordinal);
        public double RadiusMm = SelectionContextRules.DefaultRadiusMm;
        public List<string> Parameters = new List<string>();
        public int MaxFaces = SelectionContextRules.DefaultMaxFaces;
        public string Unit = "mm";
        public double LengthFromFeet = 304.8;
        public string SnapshotName;
        public string Against;
        public double ToleranceMm = SelectionContextRules.DefaultToleranceMm;
    }

    /// <summary>What a snapshot keeps of one element. Lengths in FEET (Revit's internal unit).</summary>
    public sealed class ElementFingerprint
    {
        public long Id;
        public string Category;
        public long? TypeId;
        public long? LevelId;
        /// <summary>Location samples: one point, or the two ends of a curve; null when the element has none.</summary>
        public List<double[]> Points;
        public double[] BoxMin, BoxMax;
        public double? VolumeCubicFeet;
        /// <summary>Parameter name -> value text; null value = the parameter exists and is empty; absent = not found.</summary>
        public SortedDictionary<string, string> Params = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Names of the facts that could not be read for this element.</summary>
        public List<string> Unreadable = new List<string>();
    }

    public sealed class FingerprintChange
    {
        public long Id;
        public string Category;
        public List<string> Fields = new List<string>();
        /// <summary>Largest displacement of a location sample or a box corner, feet; null when none could be compared.</summary>
        public double? MaxDisplacementFeet;
        public double? VolumeDeltaCubicFeet;
        public Dictionary<string, string[]> ParamChanges = new Dictionary<string, string[]>(StringComparer.Ordinal);
    }

    public sealed class FingerprintDiff
    {
        public List<long> Added = new List<long>();
        public List<long> Removed = new List<long>();
        public List<FingerprintChange> Changed = new List<FingerprintChange>();
        public int Unchanged;
        /// <summary>Elements present on both sides but with a fact unreadable on either: never counted as unchanged.</summary>
        public List<long> NotComparable = new List<long>();
    }

    public sealed class StoredSnapshot
    {
        public string Name;
        public string DocumentKey;
        public DateTime TakenUtc;
        public string Source;          // ids | selector | selection
        public SelectorSpec Selector;  // re-resolved by diff to find what was added
        public List<string> ParameterNames = new List<string>();
        public Dictionary<long, ElementFingerprint> Elements = new Dictionary<long, ElementFingerprint>();
        public bool Truncated;
        public string Hash;
    }

    public static class SelectionContextRules
    {
        public const double DefaultRadiusMm = 300;
        public const double MaxRadiusMm = 5000;
        public const int DefaultMaxFaces = 6;
        public const int MaxFacesCap = 60;
        public const int MaxContextElements = 50;
        public const int MaxSnapshotElements = 5000;
        public const int MaxParameters = 20;
        public const double DefaultToleranceMm = 0.5;
        public const int MaxSnapshotsPerDocument = 20;
        public const int MaxListedIds = 200;

        public static bool TryParse(JObject request, out SelectionContextRequest parsed, out string error)
        {
            parsed = null; error = null;
            var r = new SelectionContextRequest();
            string op = ((string)request["operation"] ?? SelectionContextOps.Context).Trim().ToLowerInvariant();
            if (Array.IndexOf(SelectionContextOps.All, op) < 0) { error = "operation must be one of " + string.Join(", ", SelectionContextOps.All) + "."; return false; }
            r.Operation = op;

            JToken idsTok = request["ids"];
            if (idsTok != null && idsTok.Type != JTokenType.Null)
            {
                var arr = idsTok as JArray;
                if (arr == null) { error = "ids must be an array of integer element ids."; return false; }
                foreach (JToken t in arr)
                {
                    if (t.Type != JTokenType.Integer) { error = "ids contains a value that is not an integer element id: " + t; return false; }
                    long id = (long)t;
                    if (!r.Ids.Contains(id)) r.Ids.Add(id);
                }
                int cap = op == SelectionContextOps.Context ? MaxContextElements : MaxSnapshotElements;
                if (r.Ids.Count > cap) { error = "ids carries " + r.Ids.Count + " ids; " + op + " takes at most " + cap + "."; return false; }
            }
            JToken selTok = request["selector"];
            if (selTok != null && selTok.Type != JTokenType.Null)
            {
                if (op == SelectionContextOps.Context) { error = "selector is for snapshot and diff; context reads ids or the current selection."; return false; }
                if (!ElementSelectorRules.TryParse(selTok, out r.Selector, out error)) return false;
            }
            JToken selection = request["selection"];
            if (selection != null && selection.Type != JTokenType.Null)
            {
                if (selection.Type != JTokenType.Boolean) { error = "selection must be true or false."; return false; }
                r.Selection = (bool)selection;
            }
            int sources = (r.Ids.Count > 0 ? 1 : 0) + (r.Selector != null ? 1 : 0) + (r.Selection ? 1 : 0);
            if (sources > 1) { error = "send one source of elements: ids, selector or selection=true."; return false; }
            if (sources == 0)
            {
                if (op == SelectionContextOps.Context) r.Selection = true;      // the tool's whole point
                else if (op == SelectionContextOps.Snapshot) { error = "snapshot needs ids, selector or selection=true: it never photographs the whole model by default."; return false; }
            }
            if (op == SelectionContextOps.Diff && sources > 0) { error = "diff re-reads the elements its snapshot named; send snapshot_name (and optionally against), not a source."; return false; }

            JArray sections = request["include"] as JArray;
            if (request["include"] != null && request["include"].Type != JTokenType.Null && sections == null) { error = "include must be an array."; return false; }
            if (sections != null)
            {
                if (sections.Count == 0) { error = "include must name at least one of " + string.Join(", ", SelectionContextSections.All) + "."; return false; }
                r.Sections.Clear();
                foreach (JToken t in sections)
                {
                    string s = ((string)t ?? "").Trim().ToLowerInvariant();
                    if (Array.IndexOf(SelectionContextSections.All, s) < 0) { error = "include '" + t + "' is not one of " + string.Join(", ", SelectionContextSections.All) + "."; return false; }
                    r.Sections.Add(s);
                }
            }

            JToken radius = request["radius_mm"];
            if (radius != null && radius.Type != JTokenType.Null)
            {
                if (radius.Type != JTokenType.Integer && radius.Type != JTokenType.Float) { error = "radius_mm must be a number."; return false; }
                r.RadiusMm = (double)radius;
                if (double.IsNaN(r.RadiusMm) || r.RadiusMm < 0 || r.RadiusMm > MaxRadiusMm) { error = "radius_mm must be between 0 and " + MaxRadiusMm + "."; return false; }
            }
            JToken faces = request["max_faces"];
            if (faces != null && faces.Type != JTokenType.Null)
            {
                if (faces.Type != JTokenType.Integer) { error = "max_faces must be an integer."; return false; }
                r.MaxFaces = (int)faces;
                if (r.MaxFaces < 0 || r.MaxFaces > MaxFacesCap) { error = "max_faces must be between 0 and " + MaxFacesCap + "."; return false; }
            }
            JToken tol = request["tolerance_mm"];
            if (tol != null && tol.Type != JTokenType.Null)
            {
                if (tol.Type != JTokenType.Integer && tol.Type != JTokenType.Float) { error = "tolerance_mm must be a number."; return false; }
                r.ToleranceMm = (double)tol;
                if (double.IsNaN(r.ToleranceMm) || r.ToleranceMm < 0 || r.ToleranceMm > 1000) { error = "tolerance_mm must be between 0 and 1000."; return false; }
            }
            JToken pars = request["parameters"];
            if (pars != null && pars.Type != JTokenType.Null)
            {
                var arr = pars as JArray;
                if (arr == null) { error = "parameters must be an array of parameter names."; return false; }
                foreach (JToken t in arr)
                {
                    string name = t.Type == JTokenType.String ? ((string)t).Trim() : null;
                    if (string.IsNullOrEmpty(name)) { error = "parameters holds a value that is not a non-empty name: " + t; return false; }
                    if (!r.Parameters.Contains(name)) r.Parameters.Add(name);
                }
                if (r.Parameters.Count > MaxParameters) { error = "parameters names " + r.Parameters.Count + "; the limit is " + MaxParameters + "."; return false; }
            }
            r.Unit = ((string)request["unit"] ?? "mm").Trim().ToLowerInvariant();
            if (!GeometryMeasureRules.TryUnit(r.Unit, out r.LengthFromFeet)) { error = "unit must be mm, m or feet."; return false; }

            r.SnapshotName = ((string)request["snapshot_name"])?.Trim();
            r.Against = ((string)request["against"])?.Trim();
            if (op != SelectionContextOps.Context && string.IsNullOrEmpty(r.SnapshotName)) { error = op + " needs snapshot_name."; return false; }
            if (r.SnapshotName != null && r.SnapshotName.Length > 80) { error = "snapshot_name is limited to 80 characters."; return false; }
            if (!string.IsNullOrEmpty(r.Against) && op != SelectionContextOps.Diff) { error = "against is for diff only."; return false; }
            if (!string.IsNullOrEmpty(r.Against) && string.Equals(r.Against, r.SnapshotName, StringComparison.Ordinal)) { error = "against names the same snapshot as snapshot_name; a snapshot compared with itself says nothing."; return false; }
            parsed = r;
            return true;
        }

        /// <summary>Indices of the largest faces, area descending, then index ascending; at most max.</summary>
        public static List<int> TopFaces(IList<double> areas, int max)
        {
            if (areas == null || max <= 0) return new List<int>();
            return Enumerable.Range(0, areas.Count)
                .Where(i => !double.IsNaN(areas[i]))
                .OrderByDescending(i => areas[i]).ThenBy(i => i)
                .Take(max).ToList();
        }

        /// <summary>
        /// How a neighbour stands to the element: the join relations it shares with it, read
        /// from the element's own classified joins. An empty list means "near, not joined".
        /// </summary>
        public static List<string> NeighborRelations(long neighborId, ICollection<long> joined, ICollection<long> cutBy, ICollection<long> cutting)
        {
            var list = new List<string>();
            if (joined != null && joined.Contains(neighborId)) list.Add("joined");
            if (cutBy != null && cutBy.Contains(neighborId)) list.Add("cuts_this");
            if (cutting != null && cutting.Contains(neighborId)) list.Add("cut_by_this");
            return list;
        }

        /// <summary>The eight corners of an axis-aligned box, so a box can be carried through a transform.</summary>
        public static List<double[]> Corners(double[] min, double[] max)
        {
            var list = new List<double[]>();
            for (int i = 0; i < 8; i++)
                list.Add(new[] { (i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2] });
            return list;
        }

        public static void Envelope(IEnumerable<double[]> points, out double[] min, out double[] max)
        {
            min = null; max = null;
            foreach (double[] p in points)
            {
                if (min == null) { min = (double[])p.Clone(); max = (double[])p.Clone(); continue; }
                for (int a = 0; a < 3; a++) { min[a] = Math.Min(min[a], p[a]); max[a] = Math.Max(max[a], p[a]); }
            }
        }

        // ---------------------------------------------------------------- snapshot

        /// <summary>
        /// Compare two photographs of the same elements. tolerance in FEET applies to
        /// location samples and box corners; a volume change under tolerance^3 is ignored;
        /// parameter text must be equal. An element with an unreadable fact on either side is
        /// NOT COMPARABLE, never unchanged.
        /// </summary>
        public static FingerprintDiff Compare(IDictionary<long, ElementFingerprint> before, IDictionary<long, ElementFingerprint> after, double toleranceFeet)
        {
            var diff = new FingerprintDiff();
            foreach (long id in after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k)) diff.Added.Add(id);
            foreach (long id in before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k)) diff.Removed.Add(id);
            foreach (long id in before.Keys.Where(after.ContainsKey).OrderBy(k => k))
            {
                ElementFingerprint a = before[id], b = after[id];
                var change = new FingerprintChange { Id = id, Category = b.Category ?? a.Category };
                if (!string.Equals(a.Category, b.Category, StringComparison.Ordinal)) change.Fields.Add("category");
                if (a.TypeId != b.TypeId) change.Fields.Add("type");
                if (a.LevelId != b.LevelId) change.Fields.Add("level");

                double? worst = null;
                bool locationChanged = false;
                if ((a.Points == null) != (b.Points == null) || (a.Points != null && a.Points.Count != b.Points.Count))
                    locationChanged = true;
                else if (a.Points != null)
                    for (int i = 0; i < a.Points.Count; i++)
                    {
                        double d = Distance(a.Points[i], b.Points[i]);
                        worst = Math.Max(worst ?? 0, d);
                        if (d > toleranceFeet) locationChanged = true;
                    }
                if (locationChanged) change.Fields.Add("location");

                bool boxChanged = false;
                if ((a.BoxMin == null) != (b.BoxMin == null)) boxChanged = true;
                else if (a.BoxMin != null)
                {
                    double d = Math.Max(Distance(a.BoxMin, b.BoxMin), Distance(a.BoxMax, b.BoxMax));
                    worst = Math.Max(worst ?? 0, d);
                    if (d > toleranceFeet) boxChanged = true;
                }
                if (boxChanged) change.Fields.Add("bounding_box");
                change.MaxDisplacementFeet = worst;

                if (a.VolumeCubicFeet.HasValue && b.VolumeCubicFeet.HasValue)
                {
                    double dv = b.VolumeCubicFeet.Value - a.VolumeCubicFeet.Value;
                    double floor = Math.Max(1e-9, Math.Pow(Math.Max(toleranceFeet, 1e-6), 3));
                    if (Math.Abs(dv) > floor) { change.Fields.Add("volume"); change.VolumeDeltaCubicFeet = dv; }
                }
                else if (a.VolumeCubicFeet.HasValue != b.VolumeCubicFeet.HasValue) change.Fields.Add("volume");

                foreach (string name in a.Params.Keys.Union(b.Params.Keys).OrderBy(n => n, StringComparer.Ordinal))
                {
                    bool inA = a.Params.TryGetValue(name, out string va), inB = b.Params.TryGetValue(name, out string vb);
                    if (inA != inB || !string.Equals(va, vb, StringComparison.Ordinal))
                        change.ParamChanges[name] = new[] { inA ? va : null, inB ? vb : null };
                }
                if (change.ParamChanges.Count > 0) change.Fields.Add("parameters");

                bool unreadable = a.Unreadable.Count > 0 || b.Unreadable.Count > 0;
                if (change.Fields.Count > 0) diff.Changed.Add(change);
                else if (unreadable) diff.NotComparable.Add(id);
                else diff.Unchanged++;
            }
            return diff;
        }

        public static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>A stable hash of a photograph, so two snapshots can be told equal at a glance.</summary>
        public static string Hash(IEnumerable<ElementFingerprint> elements)
        {
            var sb = new StringBuilder();
            foreach (ElementFingerprint e in elements.OrderBy(x => x.Id))
            {
                sb.Append(e.Id).Append('|').Append(e.Category).Append('|').Append(e.TypeId).Append('|').Append(e.LevelId).Append('|');
                if (e.Points != null) foreach (double[] p in e.Points) sb.Append(R(p)).Append(';');
                sb.Append('|').Append(e.BoxMin == null ? "-" : R(e.BoxMin) + R(e.BoxMax)).Append('|');
                sb.Append(e.VolumeCubicFeet.HasValue ? e.VolumeCubicFeet.Value.ToString("R", CultureInfo.InvariantCulture) : "-").Append('|');
                foreach (var kv in e.Params) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                sb.Append('\n');
            }
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private static string R(double[] p) => string.Join(",", p.Select(v => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture)));

        /// <summary>Elements per category, sorted by count then name.</summary>
        public static JObject ByCategory(IEnumerable<string> categories)
        {
            var o = new JObject();
            foreach (var g in categories.Select(c => c ?? "(no category)").GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
                o[g.Key] = g.Count();
            return o;
        }
    }

    /// <summary>
    /// Named snapshots, per document, in this Revit session only (nothing is written to the
    /// model or to disk). Bounded: at most MaxSnapshotsPerDocument per document, the oldest
    /// dropped first and the reply says which.
    /// </summary>
    public static class ElementSnapshotStore
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<StoredSnapshot>> ByDocument = new Dictionary<string, List<StoredSnapshot>>(StringComparer.OrdinalIgnoreCase);

        public static string Put(StoredSnapshot s)
        {
            lock (Gate)
            {
                if (!ByDocument.TryGetValue(s.DocumentKey, out List<StoredSnapshot> list)) ByDocument[s.DocumentKey] = list = new List<StoredSnapshot>();
                list.RemoveAll(x => string.Equals(x.Name, s.Name, StringComparison.Ordinal));
                string dropped = null;
                if (list.Count >= SelectionContextRules.MaxSnapshotsPerDocument)
                {
                    StoredSnapshot oldest = list.OrderBy(x => x.TakenUtc).First();
                    list.Remove(oldest);
                    dropped = oldest.Name;
                }
                list.Add(s);
                return dropped;
            }
        }

        public static StoredSnapshot Get(string documentKey, string name)
        {
            lock (Gate)
                return ByDocument.TryGetValue(documentKey, out List<StoredSnapshot> list)
                    ? list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))
                    : null;
        }

        public static List<string> Names(string documentKey)
        {
            lock (Gate)
                return ByDocument.TryGetValue(documentKey, out List<StoredSnapshot> list)
                    ? list.OrderBy(x => x.TakenUtc).Select(x => x.Name).ToList()
                    : new List<string>();
        }

        internal static void ClearForTests() { lock (Gate) ByDocument.Clear(); }
    }
}
