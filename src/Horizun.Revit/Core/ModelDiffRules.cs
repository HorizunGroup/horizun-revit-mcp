// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT CHANGED BETWEEN TWO DELIVERIES - the Revit-free half of horizun_model_diff.
//
// A snapshot is a list of element records (identity, classification, placement,
// normalised parameter values and a cheap geometry hash) plus the type records
// they point at. The comparison below decides added / deleted / modified from
// those records alone, so every rule a constructor reads in the report is
// provable at a desk:
//
//   IDENTITY IS THE UNIQUEID. Two records with the same UniqueId are the same
//   element. When the two snapshots share almost no UniqueIds the model was very
//   likely re-created (exported, re-modelled, copied into a new file), and a
//   report of "everything deleted, everything added" is true and useless. That
//   case is FLAGGED, never silently "fixed": the caller may ask for the
//   heuristic pairing by (category, family, type, location), and every pair it
//   produces is marked inferred=true.
//
//   A NUMBER IS COMPARED AS A NUMBER. Values are stored in Revit internal units
//   and compared with an absolute tolerance, so 3.0000000001 ft is not a change.
//
//   A MOVE IS A DISTANCE. Location (point or curve end points) is compared first;
//   without one, the bounding-box centre. Below the tolerance it is not a move.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One element as a snapshot recorded it. Short JSON names keep the file small.</summary>
    public sealed class DiffElement
    {
        [JsonProperty("u")] public string UniqueId;
        [JsonProperty("id")] public long Id;
        [JsonProperty("bic")] public string BuiltInCategory;
        [JsonProperty("cat")] public string Category;
        [JsonProperty("fam")] public string Family;
        [JsonProperty("typ")] public string Type;
        [JsonProperty("tu")] public string TypeUniqueId;
        [JsonProperty("lvl")] public string Level;
        [JsonProperty("ws")] public string Workset;
        [JsonProperty("phc")] public string PhaseCreated;
        [JsonProperty("phd")] public string PhaseDemolished;
        /// <summary>minX,minY,minZ,maxX,maxY,maxZ in internal feet, or null.</summary>
        [JsonProperty("bb")] public double[] BoundingBox;
        /// <summary>A point (3 values) or a curve's two end points (6 values), internal feet, or null.</summary>
        [JsonProperty("loc")] public double[] Location;
        [JsonProperty("geo")] public string GeometryHash;
        [JsonProperty("p")] public SortedDictionary<string, string> Parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class DiffType
    {
        [JsonProperty("u")] public string UniqueId;
        [JsonProperty("cat")] public string Category;
        [JsonProperty("fam")] public string Family;
        [JsonProperty("name")] public string Name;
        [JsonProperty("p")] public SortedDictionary<string, string> Parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class DiffSnapshot
    {
        public const string SchemaId = "horizun.model-diff-snapshot/1";
        [JsonProperty("schema")] public string Schema = SchemaId;
        [JsonProperty("id")] public string Id;
        [JsonProperty("taken_utc")] public string TakenUtc;
        [JsonProperty("document")] public JObject Document = new JObject();
        [JsonProperty("scope")] public JObject Scope = new JObject();
        [JsonProperty("truncated")] public bool Truncated;
        [JsonProperty("elements_seen")] public long ElementsSeen;
        [JsonProperty("unreadable")] public long Unreadable;
        [JsonProperty("elements")] public List<DiffElement> Elements = new List<DiffElement>();
        [JsonProperty("types")] public List<DiffType> Types = new List<DiffType>();
    }

    public sealed class DiffOptions
    {
        public double MoveToleranceFeet = 1.0 / 304.8;       // 1 mm
        public double ValueTolerance = 1e-6;                 // internal units
        public bool HeuristicMatch;
        public double MatchToleranceFeet = 50.0 / 304.8;     // 50 mm
        /// <summary>Below this share of shared UniqueIds the model is suspected re-created.</summary>
        public double RecreatedOverlap = 0.5;
        public int RecreatedMinElements = 10;
    }

    public sealed class DiffChange
    {
        [JsonProperty("field")] public string Field;
        [JsonProperty("before")] public string Before;
        [JsonProperty("after")] public string After;
    }

    public sealed class DiffRow
    {
        [JsonProperty("state")] public string State;              // added | deleted | modified
        [JsonProperty("unique_id")] public string UniqueId;
        [JsonProperty("before_unique_id", NullValueHandling = NullValueHandling.Ignore)] public string BeforeUniqueId;
        [JsonProperty("element_id")] public long ElementId;
        [JsonProperty("category")] public string Category;
        [JsonProperty("discipline")] public string Discipline;
        [JsonProperty("family")] public string Family;
        [JsonProperty("type")] public string Type;
        [JsonProperty("level")] public string Level;
        [JsonProperty("inferred")] public bool Inferred;
        [JsonProperty("moved_mm", NullValueHandling = NullValueHandling.Ignore)] public double? MovedMm;
        [JsonProperty("type_changed")] public bool TypeChanged;
        [JsonProperty("changes")] public List<DiffChange> Changes = new List<DiffChange>();
    }

    public sealed class DiffResult
    {
        public int BeforeCount, AfterCount, Shared, Unchanged;
        public bool RecreatedSuspected;
        public double Overlap;
        public int InferredPairs;
        public List<DiffRow> Rows = new List<DiffRow>();
        public List<DiffRow> TypeRows = new List<DiffRow>();

        public int Count(string state) => Rows.Count(r => r.State == state);
    }

    public static class ModelDiffRules
    {
        public const string Added = "added", Deleted = "deleted", Modified = "modified";

        // ---- value normalisation --------------------------------------------------

        /// <summary>A double in internal units, rounded so float noise is not a change.</summary>
        public static string NormalizeDouble(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "d:nan";
            double r = Math.Round(v, 9);
            if (r == 0) r = 0; // no "-0"
            return "d:" + r.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string NormalizeInteger(long v) => "i:" + v.ToString(CultureInfo.InvariantCulture);

        public static string NormalizeElementId(long v) => "e:" + v.ToString(CultureInfo.InvariantCulture);

        public const int MaxStringLength = 256;

        public static string NormalizeString(string s)
        {
            if (s == null) return "n:";
            string t = s.Trim().Replace("\r\n", "\n");
            if (t.Length > MaxStringLength) t = t.Substring(0, MaxStringLength) + "…";
            return "s:" + t;
        }

        public const string NoValue = "n:";

        /// <summary>Equal as stored values; two numbers are equal within the tolerance.</summary>
        public static bool SameValue(string a, string b, double tolerance)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (a == null || b == null) return false;
            double x, y;
            if (TryNumber(a, out x) && TryNumber(b, out y))
                return Math.Abs(x - y) <= tolerance;
            return false;
        }

        private static bool TryNumber(string s, out double v)
        {
            v = 0;
            if (s.Length < 3 || (s[0] != 'd' && s[0] != 'i') || s[1] != ':') return false;
            return double.TryParse(s.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>A stored value as a person reads it: the prefix removed.</summary>
        public static string Display(string stored)
        {
            if (stored == null) return null;
            if (stored == NoValue) return "";
            return stored.Length >= 2 && stored[1] == ':' ? stored.Substring(2) : stored;
        }

        /// <summary>Cheap geometry fingerprint: volume, area and bounding-box extents, rounded.</summary>
        public static string GeometryHash(double? volume, double? area, double[] bbox)
        {
            var sb = new StringBuilder();
            sb.Append(volume.HasValue ? Math.Round(volume.Value, 6).ToString("R", CultureInfo.InvariantCulture) : "-").Append('|');
            sb.Append(area.HasValue ? Math.Round(area.Value, 6).ToString("R", CultureInfo.InvariantCulture) : "-").Append('|');
            if (bbox != null && bbox.Length == 6)
                for (int i = 0; i < 3; i++)
                    sb.Append(Math.Round(bbox[i + 3] - bbox[i], 5).ToString("R", CultureInfo.InvariantCulture)).Append(',');
            else sb.Append('-');
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())), 0, 8)
                                   .Replace("-", "").ToLowerInvariant();
        }

        // ---- discipline -------------------------------------------------------------

        /// <summary>
        /// A discipline INFERRED from the BuiltInCategory name. Revit declares no
        /// discipline per element; the reply says so wherever this is used.
        /// </summary>
        public static string DisciplineOf(string builtInCategory)
        {
            string b = builtInCategory ?? "";
            if (b.Length == 0) return "unknown";
            if (b.IndexOf("Structural", StringComparison.Ordinal) >= 0 || b.StartsWith("OST_Rebar", StringComparison.Ordinal) ||
                b.StartsWith("OST_StructConnection", StringComparison.Ordinal) || b == "OST_Truss" || b == "OST_FabricAreas" ||
                b == "OST_FabricReinforcement" || b == "OST_AreaRein" || b == "OST_PathRein")
                return "structure";
            string[] mech = { "OST_Duct", "OST_FlexDuct", "OST_MechanicalEquipment", "OST_PlaceHolderDucts", "OST_HVAC", "OST_MechanicalControlDevices" };
            if (mech.Any(p => b.StartsWith(p, StringComparison.Ordinal))) return "mechanical";
            string[] plumb = { "OST_Pipe", "OST_FlexPipe", "OST_PlumbingFixtures", "OST_PlumbingEquipment", "OST_Sprinklers", "OST_PlaceHolderPipes" };
            if (plumb.Any(p => b.StartsWith(p, StringComparison.Ordinal))) return "plumbing";
            string[] elec = { "OST_Electrical", "OST_Lighting", "OST_Cable", "OST_Conduit", "OST_Communication", "OST_DataDevices",
                              "OST_SecurityDevices", "OST_FireAlarmDevices", "OST_NurseCallDevices", "OST_TelephoneDevices",
                              "OST_Wire", "OST_AudioVisualDevices" };
            if (elec.Any(p => b.StartsWith(p, StringComparison.Ordinal))) return "electrical";
            if (b.StartsWith("OST_Topo", StringComparison.Ordinal) || b == "OST_Site" || b == "OST_Planting" || b == "OST_Roads")
                return "site";
            return "architecture";
        }

        // ---- identifiers and storage --------------------------------------------------

        public static string NewId(DateTime utc, string seed)
        {
            string stamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            using (var sha = SHA256.Create())
            {
                string h = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes((seed ?? "") + "|" + utc.Ticks)), 0, 4)
                                       .Replace("-", "").ToLowerInvariant();
                return stamp + "-" + h;
            }
        }

        /// <summary>Only ids this code minted may name a file; anything else could walk out of the directory.</summary>
        public static bool IsValidId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
            return true;
        }

        public static byte[] Serialize(DiffSnapshot s)
        {
            string json = JsonConvert.SerializeObject(s, Formatting.None,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    gz.Write(bytes, 0, bytes.Length);
                }
                return ms.ToArray();
            }
        }

        public static DiffSnapshot Deserialize(byte[] gz)
        {
            using (var ms = new MemoryStream(gz))
            using (var z = new GZipStream(ms, CompressionMode.Decompress))
            using (var reader = new StreamReader(z, Encoding.UTF8))
            {
                var s = JsonConvert.DeserializeObject<DiffSnapshot>(reader.ReadToEnd(),
                    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
                if (s == null || s.Schema != DiffSnapshot.SchemaId)
                    throw new InvalidDataException("not a " + DiffSnapshot.SchemaId + " snapshot");
                if (s.Elements == null) s.Elements = new List<DiffElement>();
                if (s.Types == null) s.Types = new List<DiffType>();
                return s;
            }
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The metadata a listing reads without decompressing the snapshot.</summary>
        public static JObject Meta(DiffSnapshot s, string sha256, long bytes)
        {
            var byCategory = new JObject();
            foreach (var g in s.Elements.GroupBy(e => e.Category ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal))
                byCategory[g.Key] = g.Count();
            return new JObject
            {
                ["schema"] = "horizun.model-diff-snapshot-meta/1",
                ["id"] = s.Id,
                ["taken_utc"] = s.TakenUtc,
                ["document"] = s.Document.DeepClone(),
                ["scope"] = s.Scope.DeepClone(),
                ["elements"] = s.Elements.Count,
                ["types"] = s.Types.Count,
                ["elements_seen"] = s.ElementsSeen,
                ["unreadable"] = s.Unreadable,
                ["truncated"] = s.Truncated,
                ["by_category"] = byCategory,
                ["content_sha256"] = sha256,
                ["bytes"] = bytes
            };
        }

        // ---- comparison -----------------------------------------------------------------

        public static DiffResult Compare(DiffSnapshot before, DiffSnapshot after, DiffOptions o)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            o = o ?? new DiffOptions();
            var b = Index(before.Elements);
            var a = Index(after.Elements);
            var bTypes = before.Types.Where(t => t.UniqueId != null).GroupBy(t => t.UniqueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var aTypes = after.Types.Where(t => t.UniqueId != null).GroupBy(t => t.UniqueId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var r = new DiffResult { BeforeCount = b.Count, AfterCount = a.Count };
            var deleted = new List<DiffElement>();
            var added = new List<DiffElement>();

            foreach (var kv in b)
            {
                DiffElement now;
                if (!a.TryGetValue(kv.Key, out now)) { deleted.Add(kv.Value); continue; }
                r.Shared++;
                DiffRow row = Changed(kv.Value, now, o, false);
                if (row == null) r.Unchanged++; else r.Rows.Add(row);
            }
            foreach (var kv in a)
                if (!b.ContainsKey(kv.Key)) added.Add(kv.Value);

            int smaller = Math.Min(b.Count, a.Count);
            r.Overlap = smaller == 0 ? 1.0 : (double)r.Shared / smaller;
            r.RecreatedSuspected = smaller >= o.RecreatedMinElements && r.Overlap < o.RecreatedOverlap;

            if (o.HeuristicMatch)
            {
                foreach (var pair in Pair(deleted, added, o.MatchToleranceFeet))
                {
                    r.InferredPairs++;
                    deleted.Remove(pair.Key);
                    added.Remove(pair.Value);
                    DiffRow row = Changed(pair.Key, pair.Value, o, true);
                    if (row == null) r.Unchanged++; else r.Rows.Add(row);
                }
            }

            foreach (DiffElement d in deleted) r.Rows.Add(Whole(Deleted, d));
            foreach (DiffElement n in added) r.Rows.Add(Whole(Added, n));

            // Types: a changed type parameter changes every instance of it, so it is
            // reported once, on the type, rather than smeared over thousands of rows.
            foreach (var kv in bTypes)
            {
                DiffType now;
                if (!aTypes.TryGetValue(kv.Key, out now)) continue;
                var changes = ParamChanges(kv.Value.Parameters, now.Parameters, o.ValueTolerance, "type_param:");
                if (kv.Value.Name != now.Name) changes.Insert(0, new DiffChange { Field = "type_name", Before = kv.Value.Name, After = now.Name });
                if (changes.Count == 0) continue;
                r.TypeRows.Add(new DiffRow
                {
                    State = Modified, UniqueId = kv.Key, Category = now.Category, Family = now.Family, Type = now.Name,
                    Discipline = null, Changes = changes
                });
            }

            r.Rows = r.Rows.OrderBy(x => StateOrder(x.State)).ThenBy(x => x.Category ?? "", StringComparer.Ordinal)
                           .ThenBy(x => x.UniqueId ?? "", StringComparer.Ordinal).ToList();
            return r;
        }

        private static int StateOrder(string s) => s == Added ? 0 : s == Deleted ? 1 : 2;

        private static Dictionary<string, DiffElement> Index(List<DiffElement> list)
        {
            var d = new Dictionary<string, DiffElement>(StringComparer.Ordinal);
            foreach (DiffElement e in list ?? new List<DiffElement>())
                if (!string.IsNullOrEmpty(e?.UniqueId) && !d.ContainsKey(e.UniqueId)) d[e.UniqueId] = e;
            return d;
        }

        private static DiffRow Whole(string state, DiffElement e) => new DiffRow
        {
            State = state, UniqueId = e.UniqueId, ElementId = e.Id, Category = e.Category,
            Discipline = DisciplineOf(e.BuiltInCategory), Family = e.Family, Type = e.Type, Level = e.Level
        };

        /// <summary>The row describing what changed between two records of one element, or null when nothing did.</summary>
        public static DiffRow Changed(DiffElement was, DiffElement now, DiffOptions o, bool inferred)
        {
            var changes = new List<DiffChange>();
            bool typeChanged = !string.Equals(was.TypeUniqueId, now.TypeUniqueId, StringComparison.Ordinal) ||
                               !string.Equals(was.Family, now.Family, StringComparison.Ordinal) ||
                               !string.Equals(was.Type, now.Type, StringComparison.Ordinal);
            if (typeChanged)
                changes.Add(new DiffChange { Field = "type", Before = was.Family + " : " + was.Type, After = now.Family + " : " + now.Type });
            Attr(changes, "level", was.Level, now.Level);
            Attr(changes, "workset", was.Workset, now.Workset);
            Attr(changes, "phase_created", was.PhaseCreated, now.PhaseCreated);
            Attr(changes, "phase_demolished", was.PhaseDemolished, now.PhaseDemolished);

            double? moved = Moved(was, now);
            bool isMove = moved.HasValue && moved.Value > o.MoveToleranceFeet;
            if (isMove)
                changes.Add(new DiffChange { Field = "location", Before = Coords(was.Location ?? Center(was.BoundingBox)),
                                             After = Coords(now.Location ?? Center(now.BoundingBox)) });
            if (!string.Equals(was.GeometryHash, now.GeometryHash, StringComparison.Ordinal))
                changes.Add(new DiffChange { Field = "geometry", Before = was.GeometryHash, After = now.GeometryHash });
            changes.AddRange(ParamChanges(was.Parameters, now.Parameters, o.ValueTolerance, "param:"));

            if (changes.Count == 0 && !inferred) return null;
            if (changes.Count == 0 && inferred)
                changes.Add(new DiffChange { Field = "unique_id", Before = was.UniqueId, After = now.UniqueId });
            return new DiffRow
            {
                State = Modified, UniqueId = now.UniqueId, BeforeUniqueId = inferred ? was.UniqueId : null,
                ElementId = now.Id, Category = now.Category, Discipline = DisciplineOf(now.BuiltInCategory),
                Family = now.Family, Type = now.Type, Level = now.Level, Inferred = inferred,
                MovedMm = isMove ? Math.Round(moved.Value * 304.8, 2) : (double?)null,
                TypeChanged = typeChanged, Changes = changes
            };
        }

        private static void Attr(List<DiffChange> changes, string field, string was, string now)
        {
            if (!string.Equals(was ?? "", now ?? "", StringComparison.Ordinal))
                changes.Add(new DiffChange { Field = field, Before = was, After = now });
        }

        public static List<DiffChange> ParamChanges(IDictionary<string, string> was, IDictionary<string, string> now,
                                                    double tolerance, string prefix)
        {
            var list = new List<DiffChange>();
            was = was ?? new SortedDictionary<string, string>();
            now = now ?? new SortedDictionary<string, string>();
            foreach (string key in was.Keys.Union(now.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                string x, y;
                was.TryGetValue(key, out x);
                now.TryGetValue(key, out y);
                if (x != null && y != null && SameValue(x, y, tolerance)) continue;
                list.Add(new DiffChange { Field = prefix + key, Before = x == null ? null : Display(x), After = y == null ? null : Display(y) });
            }
            return list;
        }

        /// <summary>Distance moved in feet: location first, else bounding-box centre; null when neither compares.</summary>
        public static double? Moved(DiffElement was, DiffElement now)
        {
            if (was.Location != null && now.Location != null && was.Location.Length == now.Location.Length && was.Location.Length % 3 == 0)
            {
                double max = 0;
                for (int i = 0; i < was.Location.Length; i += 3)
                    max = Math.Max(max, Dist(was.Location, i, now.Location, i));
                return max;
            }
            double[] c0 = Center(was.BoundingBox), c1 = Center(now.BoundingBox);
            if (c0 != null && c1 != null) return Dist(c0, 0, c1, 0);
            return null;
        }

        private static double Dist(double[] a, int i, double[] b, int j)
        {
            double dx = a[i] - b[j], dy = a[i + 1] - b[j + 1], dz = a[i + 2] - b[j + 2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static double[] Center(double[] bb)
            => bb == null || bb.Length != 6 ? null : new[] { (bb[0] + bb[3]) / 2, (bb[1] + bb[4]) / 2, (bb[2] + bb[5]) / 2 };

        /// <summary>The point an element is paired by: first location point, else bounding-box centre.</summary>
        public static double[] Anchor(DiffElement e)
        {
            if (e.Location != null && e.Location.Length >= 3)
            {
                if (e.Location.Length == 6)
                    return new[] { (e.Location[0] + e.Location[3]) / 2, (e.Location[1] + e.Location[4]) / 2, (e.Location[2] + e.Location[5]) / 2 };
                return new[] { e.Location[0], e.Location[1], e.Location[2] };
            }
            return Center(e.BoundingBox);
        }

        private static string Coords(double[] v)
        {
            if (v == null) return null;
            return string.Join(",", v.Select(x => Math.Round(x * 304.8, 1).ToString("0.#", CultureInfo.InvariantCulture))) + " mm";
        }

        /// <summary>
        /// Greedy nearest pairing of deleted with added records sharing category, family
        /// and type, within the tolerance. Deterministic: candidates are visited in
        /// UniqueId order and ties go to the lower UniqueId. Every pair is an INFERENCE.
        /// </summary>
        public static List<KeyValuePair<DiffElement, DiffElement>> Pair(List<DiffElement> deleted, List<DiffElement> added, double toleranceFeet)
        {
            var pairs = new List<KeyValuePair<DiffElement, DiffElement>>();
            var pool = added.OrderBy(x => x.UniqueId, StringComparer.Ordinal)
                            .GroupBy(x => Key(x)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (DiffElement d in deleted.OrderBy(x => x.UniqueId, StringComparer.Ordinal))
            {
                List<DiffElement> candidates;
                if (!pool.TryGetValue(Key(d), out candidates) || candidates.Count == 0) continue;
                double[] p = Anchor(d);
                if (p == null) continue;
                DiffElement best = null; double bestDist = double.MaxValue;
                foreach (DiffElement c in candidates)
                {
                    double[] q = Anchor(c);
                    if (q == null) continue;
                    double dist = Dist(p, 0, q, 0);
                    if (dist <= toleranceFeet && dist < bestDist) { best = c; bestDist = dist; }
                }
                if (best == null) continue;
                candidates.Remove(best);
                pairs.Add(new KeyValuePair<DiffElement, DiffElement>(d, best));
            }
            return pairs;
        }

        private static string Key(DiffElement e) => (e.Category ?? "") + "\u001f" + (e.Family ?? "") + "\u001f" + (e.Type ?? "");

        // ---- reporting --------------------------------------------------------------------

        public static JObject Summary(DiffResult r)
        {
            var o = new JObject
            {
                ["before_elements"] = r.BeforeCount,
                ["after_elements"] = r.AfterCount,
                ["added"] = r.Count(Added),
                ["deleted"] = r.Count(Deleted),
                ["modified"] = r.Count(Modified),
                ["unchanged"] = r.Unchanged,
                ["moved"] = r.Rows.Count(x => x.MovedMm.HasValue),
                ["type_changed"] = r.Rows.Count(x => x.TypeChanged),
                ["types_modified"] = r.TypeRows.Count,
                ["inferred_pairs"] = r.InferredPairs,
                ["shared_unique_ids"] = r.Shared,
                ["unique_id_overlap"] = Math.Round(r.Overlap, 4),
                ["by_category"] = Group(r.Rows, x => x.Category),
                ["by_discipline"] = Group(r.Rows, x => x.Discipline),
                ["by_level"] = Group(r.Rows, x => x.Level)
            };
            if (r.RecreatedSuspected)
                o["identity_warning"] =
                    "Only " + Math.Round(r.Overlap * 100, 1).ToString(CultureInfo.InvariantCulture) + "% of UniqueIds are " +
                    "shared: the model was very likely re-created, so added/deleted here mostly mean 're-identified'. " +
                    "heuristic_match=true pairs elements by category, family, type and location; those pairs are " +
                    "marked inferred=true and are an inference, not identity.";
            return o;
        }

        private static JObject Group(List<DiffRow> rows, Func<DiffRow, string> key)
        {
            var o = new JObject();
            foreach (var g in rows.GroupBy(x => key(x) ?? "(none)").OrderBy(g => g.Key, StringComparer.Ordinal))
                o[g.Key] = new JObject
                {
                    ["added"] = g.Count(x => x.State == Added),
                    ["deleted"] = g.Count(x => x.State == Deleted),
                    ["modified"] = g.Count(x => x.State == Modified)
                };
            return o;
        }

        public static JObject Page(List<DiffRow> rows, int offset, int limit)
        {
            if (offset < 0) offset = 0;
            if (limit < 1) limit = 1;
            var slice = rows.Skip(offset).Take(limit).ToList();
            int next = offset + slice.Count;
            return new JObject
            {
                ["total"] = rows.Count,
                ["offset"] = offset,
                ["returned"] = slice.Count,
                ["truncated"] = next < rows.Count,
                ["next_offset"] = next < rows.Count ? (JToken)next : JValue.CreateNull(),
                ["rows"] = JArray.FromObject(slice, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }))
            };
        }

        /// <summary>One CSV line per change (one per element for added/deleted). RFC 4180 quoting.</summary>
        public static string Csv(DiffResult r)
        {
            var sb = new StringBuilder();
            sb.Append("state,unique_id,before_unique_id,element_id,category,discipline,family,type,level,inferred,field,before,after\r\n");
            foreach (DiffRow row in r.Rows.Concat(r.TypeRows.Select(t => new DiffRow
                     {
                         State = "type_modified", UniqueId = t.UniqueId, Category = t.Category, Family = t.Family,
                         Type = t.Type, Changes = t.Changes
                     })))
            {
                var changes = row.Changes.Count == 0 ? new List<DiffChange> { new DiffChange() } : row.Changes;
                foreach (DiffChange c in changes)
                {
                    sb.Append(string.Join(",", new[]
                    {
                        row.State, row.UniqueId, row.BeforeUniqueId, row.ElementId == 0 ? "" : row.ElementId.ToString(CultureInfo.InvariantCulture),
                        row.Category, row.Discipline, row.Family, row.Type, row.Level, row.Inferred ? "true" : "false",
                        c.Field, c.Before, c.After
                    }.Select(CsvCell))).Append("\r\n");
                }
            }
            return sb.ToString();
        }

        public static string CsvCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // A leading formula character is neutralised: a spreadsheet must not execute a parameter value.
            if ("=+-@".IndexOf(s[0]) >= 0 && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
