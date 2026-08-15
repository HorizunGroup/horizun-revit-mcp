// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Did the geometry move? Measured, per dimension, with what was NOT measured
// named alongside what was.
//
// family_apply shipped a field called `geometry_invariant` whose value could be
// "proven_unchanged". What it actually compared was the COUNT of Double
// parameters and whether a parameter named IsCustom still existed. Both are
// worth checking - a family script once broke geometry exactly that way - but
// neither is geometry:
//
//   * change a Double's VALUE and the count is identical. The extrusion moves.
//   * swap one Double for another and the count is identical.
//   * drive a dimension from a formula and no parameter changes at all.
//
// So the old check passes, the field says proven_unchanged, and the family is a
// different shape. A guarantee that can be satisfied by something other than the
// thing it names is worse than no guarantee: it is believed.
//
// This file measures the shape. A signature is a set of DIMENSIONS, each of which
// is a number we either got or did not:
//
//   bounding box   - the envelope, in each axis
//   solid volume   - what is actually there
//   surface area   - catches a shape change that preserves volume
//   solid count    - a form split in two keeps volume and area
//   connectors     - count, and each one's position and direction
//
// A dimension that could not be read is UNMEASURED and stays that way in the
// verdict. "Unchanged" is only ever said about dimensions that were compared,
// and the verdict names how many were not - so a partial check can never be read
// as a whole one.
//
// Revit-free: the comparison and its tolerances are provable without a family.
// Capturing the numbers needs Revit; deciding what they mean does not.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One measurable aspect of a shape, or the reason it is missing.</summary>
    public sealed class GeoDimension
    {
        public string Name { get; }
        public double? Value { get; }
        public string Unmeasuredbecause { get; }

        private GeoDimension(string name, double? value, string why)
        {
            Name = name; Value = value; Unmeasuredbecause = why;
        }

        public static GeoDimension Of(string name, double value) => new GeoDimension(name, value, null);
        public static GeoDimension Unmeasured(string name, string why) => new GeoDimension(name, null, why ?? "not read");

        public bool IsMeasured => Value.HasValue;
    }

    /// <summary>The shape of one family type, as far as it could be measured.</summary>
    public sealed class GeometrySignature
    {
        public string TypeName { get; set; }
        public List<GeoDimension> Dimensions { get; } = new List<GeoDimension>();

        /// <summary>Connector fingerprints, or null when connectors could not be read at all.</summary>
        public List<string> Connectors { get; set; }

        public void Add(GeoDimension d) { if (d != null) Dimensions.Add(d); }

        public GeoDimension Find(string name) =>
            Dimensions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The unit each dimension is measured in (story 5.17). The numbers come out of
    /// the Revit API in its internal units - decimal FEET, whatever the document
    /// displays - and the baseline published them bare. Verified in the field on a
    /// 15x15x10 cm box: solid_volume and surface_area read back exactly in ft3/ft2,
    /// and a human reading 0.0794 next to "volume" with no unit has every reason to
    /// call it wrong. The comparison never cared (same units on both sides); the
    /// names are for the reader.
    /// </summary>
    public static class GeoUnits
    {
        public const string Note =
            "All numbers are in Revit's INTERNAL units - decimal feet - whatever the document displays: " +
            "lengths in ft, areas in ft2, volumes in ft3. A 15x15x10 cm box reads solid_volume ~0.0794 ft3. " +
            "The before/after comparison is unaffected (both sides share the units); the unit is named so a " +
            "human reading a single number is not misled.";

        /// <summary>
        /// Accepts a bare dimension name ("bbox_x") or a type-qualified one
        /// ("Caja 15x15.bbox_x"); the dimension is whatever follows the last dot.
        /// Unknown names answer "unknown", never a guess.
        /// </summary>
        public static string Of(string dimensionName)
        {
            if (string.IsNullOrEmpty(dimensionName)) return "unknown";
            string n = dimensionName;
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
            switch (n)
            {
                case "solid_volume": return "ft3";
                case "surface_area": return "ft2";
                case "bbox_x":
                case "bbox_y":
                case "bbox_z": return "ft";
                case "solid_count":
                case "connector_count": return "count";
                case "connectors": return "ft (positions); unitless (directions)";
                default: return "unknown";
            }
        }
    }

    public sealed class GeoChange
    {
        public string Dimension { get; internal set; }
        public double? Before { get; internal set; }
        public double? After { get; internal set; }
        public string Detail { get; internal set; }

        public string Describe()
        {
            if (Before.HasValue && After.HasValue)
            {
                string unit = GeoUnits.Of(Dimension);
                return Dimension + ": " + Before.Value.ToString("0.######", CultureInfo.InvariantCulture) +
                       " -> " + After.Value.ToString("0.######", CultureInfo.InvariantCulture) +
                       (unit == "unknown" ? "" : " " + unit);
            }
            return Dimension + ": " + (Detail ?? "changed");
        }
    }

    public sealed class GeometryVerdict
    {
        /// <summary>Dimensions compared on both sides and equal within tolerance.</summary>
        public int Unchanged { get; internal set; }

        /// <summary>Dimensions compared on both sides and different.</summary>
        public List<GeoChange> Changed { get; } = new List<GeoChange>();

        /// <summary>Dimensions missing on either side, with the reason, per type.</summary>
        public List<string> NotVerified { get; } = new List<string>();

        /// <summary>Types present on one side only.</summary>
        public List<string> TypesAdded { get; } = new List<string>();
        public List<string> TypesRemoved { get; } = new List<string>();

        /// <summary>
        /// Renames the CALLER declared and this comparison therefore paired instead of reading
        /// as a disappearance plus an arrival. Reported so the pairing is visible rather than
        /// silent: a reader can see exactly which identity was carried across, and a rename that
        /// should not have happened is on the record instead of being smoothed away.
        /// A rename is NOT a geometry change - it is the same shape under a new name - so this
        /// list deliberately does not feed AnyChange.
        /// </summary>
        public List<string> TypesRenamed { get; } = new List<string>();

        public bool AnyChange => Changed.Count > 0 || TypesAdded.Count > 0 || TypesRemoved.Count > 0;
        public bool FullyVerified => NotVerified.Count == 0;

        /// <summary>
        /// True when not a single dimension was compared on both sides. A verdict built
        /// out of zero comparisons has measured nothing, and NOTHING measured must not
        /// wear the same word as a clean pass (story 5.18) - the same rule the live
        /// harness enforces for empty tables: an empty table is not agreement.
        /// </summary>
        public bool NothingCompared => Unchanged == 0 && Changed.Count == 0;

        /// <summary>
        /// The only four answers this may give. "unchanged" requires that every dimension
        /// on every type was actually compared - anything less is "unchanged_where_measured",
        /// which is a different sentence and must read like one. And either of those
        /// requires that SOMETHING was compared: zero comparisons is "unproven", because
        /// two empty captures agree the way two blank pages agree. "changed" stays first -
        /// a type that appeared or vanished is an observation even when no dimension was
        /// comparable.
        /// </summary>
        public string Status =>
            AnyChange ? "changed"
            : NothingCompared ? "unproven"
            : FullyVerified ? "unchanged"
            : "unchanged_where_measured";

        public string Summary()
        {
            if (AnyChange)
            {
                var what = Changed.Take(6).Select(c => c.Describe()).ToList();
                if (TypesAdded.Count > 0) what.Add(TypesAdded.Count + " type(s) appeared");
                if (TypesRemoved.Count > 0) what.Add(TypesRemoved.Count + " type(s) disappeared");
                string s = "The geometry CHANGED: " + string.Join("; ", what) +
                           (Changed.Count > 6 ? " (and " + (Changed.Count - 6) + " more)" : "") + ".";
                if (!FullyVerified)
                    s += " " + NotVerified.Count + " further dimension(s) could not be measured at all.";
                return s;
            }

            if (NothingCompared)
                return "NOTHING was compared: zero dimensions were measured on both sides" +
                       (NotVerified.Count > 0
                           ? " (" + NotVerified.Count + " could not be measured)"
                           : " (no measured dimension existed on either side)") +
                       ". This is not evidence that the shape held - it was not looked at, and " +
                       "'unchanged' is deliberately not said here.";

            if (FullyVerified)
                return "Every measured dimension of every type is identical before and after: " + Unchanged +
                       " comparison(s), none changed.";

            return "No CHANGE was detected in the " + Unchanged + " dimension(s) that could be compared, but " +
                   NotVerified.Count + " could NOT be measured, so the geometry is NOT proven unchanged - only " +
                   "unchanged where it was looked at.";
        }
    }

    public static class GeometryCompare
    {
        /// <summary>
        /// Relative tolerance. Regeneration reshuffles floating point at the last bits, so
        /// an exact compare would report a change on every run; anything above this is a
        /// real move. Absolute floor for values near zero.
        /// </summary>
        public const double RelativeTolerance = 1e-6;
        public const double AbsoluteFloor = 1e-9;

        public static bool SameNumber(double a, double b)
        {
            double diff = Math.Abs(a - b);
            if (diff <= AbsoluteFloor) return true;
            double scale = Math.Max(Math.Abs(a), Math.Abs(b));
            if (scale <= AbsoluteFloor) return diff <= AbsoluteFloor;
            return diff / scale <= RelativeTolerance;
        }

        public static GeometryVerdict Compare(IList<GeometrySignature> before, IList<GeometrySignature> after)
        {
            return Compare(before, after, null);
        }

        /// <summary>
        /// <paramref name="appliedRenames"/> maps a type's name BEFORE to its name AFTER, for
        /// renames the caller PERFORMED and verified in this same transaction. Omit it (or pass
        /// null) and nothing is assumed - the comparison falls back to matching purely by name.
        ///
        /// Why this parameter exists, since the old comment here said the opposite. Matching by
        /// name alone reports a renamed type as one gone plus one arrived, and family_apply's own
        /// `family_name` option renames the surviving type - so the command tripped its own guard
        /// on work it had just been told to do: verdict "changed" with dimensions_compared = 0,
        /// the whole transaction rolled back, and NOTHING measured. A guard that cannot find its
        /// subject has not found a change; it has failed to look.
        ///
        /// The old comment's fear was real and is still honoured: guessing a rename from names
        /// alone WOULD let a deletion hide behind one. This does not guess. The mapping is not
        /// inferred from the two snapshots - it is supplied by the caller, which knows the rename
        /// because it executed it and read the new name back off the model. A type that vanished
        /// without such a mapping is still reported as removed, exactly as before.
        /// </summary>
        public static GeometryVerdict Compare(IList<GeometrySignature> before, IList<GeometrySignature> after,
                                              IDictionary<string, string> appliedRenames)
        {
            var v = new GeometryVerdict();
            before = before ?? new List<GeometrySignature>();
            after = after ?? new List<GeometrySignature>();

            var beforeByName = new Dictionary<string, GeometrySignature>(StringComparer.Ordinal);
            foreach (var s in before)
            {
                if (s?.TypeName == null) continue;
                // File the BEFORE signature under the name that type is expected to answer to
                // AFTER, so the two sides line up on the same subject.
                string key = s.TypeName;
                if (appliedRenames != null && appliedRenames.TryGetValue(key, out string renamedTo) &&
                    !string.IsNullOrEmpty(renamedTo))
                {
                    key = renamedTo;
                    v.TypesRenamed.Add(s.TypeName + " -> " + renamedTo);
                }
                beforeByName[key] = s;
            }
            var afterByName = new Dictionary<string, GeometrySignature>(StringComparer.Ordinal);
            foreach (var s in after) if (s?.TypeName != null) afterByName[s.TypeName] = s;

            // Whatever is left unmatched really is one-sided: a type that went away, or one that
            // turned up. A rename the caller declared has already been paired above.
            foreach (string name in beforeByName.Keys)
                if (!afterByName.ContainsKey(name)) v.TypesRemoved.Add(name);
            foreach (string name in afterByName.Keys)
                if (!beforeByName.ContainsKey(name)) v.TypesAdded.Add(name);

            foreach (var kv in beforeByName)
            {
                if (!afterByName.TryGetValue(kv.Key, out GeometrySignature b)) continue;
                GeometrySignature a = kv.Value;

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in a.Dimensions) names.Add(d.Name);
                foreach (var d in b.Dimensions) names.Add(d.Name);

                foreach (string dim in names.OrderBy(n => n, StringComparer.Ordinal))
                {
                    GeoDimension x = a.Find(dim), y = b.Find(dim);

                    if (x == null || y == null || !x.IsMeasured || !y.IsMeasured)
                    {
                        string why = x == null ? "absent before"
                                   : y == null ? "absent after"
                                   : !x.IsMeasured ? ("before: " + x.Unmeasuredbecause)
                                   : ("after: " + y.Unmeasuredbecause);
                        v.NotVerified.Add(kv.Key + "." + dim + " (" + why + ")");
                        continue;
                    }

                    if (SameNumber(x.Value.Value, y.Value.Value)) v.Unchanged++;
                    else v.Changed.Add(new GeoChange
                    {
                        Dimension = kv.Key + "." + dim,
                        Before = x.Value,
                        After = y.Value
                    });
                }

                CompareConnectors(kv.Key, a, b, v);
            }

            return v;
        }

        private static void CompareConnectors(string typeName, GeometrySignature a, GeometrySignature b, GeometryVerdict v)
        {
            if (a.Connectors == null || b.Connectors == null)
            {
                v.NotVerified.Add(typeName + ".connectors (" +
                                  (a.Connectors == null ? "not read before" : "not read after") + ")");
                return;
            }

            // Sorted, so the same connectors enumerated in a different order are the same
            // set - Revit does not promise enumeration order across a regenerate.
            var x = a.Connectors.OrderBy(s => s, StringComparer.Ordinal).ToList();
            var y = b.Connectors.OrderBy(s => s, StringComparer.Ordinal).ToList();

            if (x.Count != y.Count)
            {
                v.Changed.Add(new GeoChange
                {
                    Dimension = typeName + ".connector_count",
                    Before = x.Count,
                    After = y.Count
                });
                return;
            }

            for (int i = 0; i < x.Count; i++)
            {
                if (string.Equals(x[i], y[i], StringComparison.Ordinal)) continue;
                v.Changed.Add(new GeoChange
                {
                    Dimension = typeName + ".connectors",
                    Detail = "a connector moved or changed direction (" + x[i] + " -> " + y[i] + ")"
                });
                return;
            }

            v.Unchanged++;
        }
    }
}
