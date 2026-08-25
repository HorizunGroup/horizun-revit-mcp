// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// EVERY DECISION ABOUT A DIMENSION REQUEST THAT DOES NOT NEED A BUILDING.
//
// horizun_annotate grew from one dimension operation to seven, and each one has
// its own required fields, its own reference arithmetic, and its own idea of
// which options make sense. Spelled inline per operation, those rules would be
// seven chances to get one of them subtly different - the same shape of defect
// FallbackDecision was extracted to end - and none of them would be provable
// without a Revit in the room.
//
// So the rules live here, Revit-free, and the command only GATHERS facts:
//
//   * the conditional requirements table (which fields each operation demands);
//   * which options an operation carries at all, and which a reference count
//     makes eligible (a prefix on a three-segment chain has no single segment
//     to sit on);
//   * the reference-list arithmetic: bounds, empty entries, exact duplicates;
//   * the canonical 0.1 mm rounding and the SHA-256 fingerprint of a
//     reference's geometric facts - the thing that makes "the face moved after
//     the dry run" a detectable event instead of a hope;
//   * curve comparison with a NAMED tolerance, so a verification can say what
//     "the same line" meant;
//   * the final-state decision over Revit's REAL TransactionStatus values,
//     including the one a live Revit will not produce on demand: a rollback
//     that did not confirm, which is uncertainty and must never be smoothed
//     into a clean model;
//   * the unit factors, and the refusal texts for operations whose API simply
//     does not exist in a given Revit - texts that must NOT travel as
//     UnsupportedCapability, because that type GRANTS the Python fallback and
//     Python cannot call an absent class any more than we can.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class DimensionPlanRules
    {
        // ---- the operations, spelled once -----------------------------------
        public const string OpText = "text";
        public const string OpTag = "tag";
        public const string OpDimension = "dimension";
        public const string OpAngular = "angular_dimension";
        public const string OpRadial = "radial_dimension";
        public const string OpDiameter = "diameter_dimension";
        public const string OpArcLength = "arc_length_dimension";
        public const string OpSpotElevation = "spot_elevation";
        public const string OpSpotCoordinate = "spot_coordinate";
        public const string OpSpotSlope = "spot_slope";

        /// <summary>
        /// The operations that produce a dimension element. spot_slope is deliberately
        /// NOT here: it is in the contract's enum (so it refuses with an explanation
        /// instead of reading as a capability gap) but no Revit API can create one.
        /// </summary>
        public static readonly string[] DimensionOperations =
        {
            OpDimension, OpAngular, OpRadial, OpDiameter, OpArcLength,
            OpSpotElevation, OpSpotCoordinate
        };

        public static bool IsDimensionOperation(string op)
        {
            foreach (string known in DimensionOperations)
                if (string.Equals(known, op, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Everything the contract's enum names, including the two legacy ones.</summary>
        public static bool IsKnownOperation(string op)
            => string.Equals(op, OpText, StringComparison.Ordinal)
            || string.Equals(op, OpTag, StringComparison.Ordinal)
            || string.Equals(op, OpSpotSlope, StringComparison.Ordinal)
            || IsDimensionOperation(op);

        // ---- the conditional requirements table -----------------------------

        /// <summary>
        /// The fields an operation REQUIRES, as one pure table. Null for an operation
        /// outside the enum - "we do not know its shape" is a different answer from
        /// "it needs nothing", and collapsing them would validate garbage as complete.
        /// spot_slope returns an empty list: it is refused before any field is read.
        /// </summary>
        public static IReadOnlyList<string> RequiredFields(string op)
        {
            switch (op)
            {
                case OpText: return new[] { "view_id", "point", "text", "text_type_id" };
                case OpTag: return new[] { "view_id", "point", "element_id" };
                case OpDimension: return new[] { "view_id", "line_start", "line_end", "references" };
                case OpAngular: return new[] { "view_id", "arc_center", "arc_radius", "references" };
                case OpRadial: return new[] { "view_id", "reference" };
                case OpDiameter: return new[] { "view_id", "reference" };
                case OpArcLength: return new[] { "view_id", "arc_center", "arc_radius", "arc_reference", "references" };
                case OpSpotElevation: return new[] { "view_id", "reference", "point" };
                case OpSpotCoordinate: return new[] { "view_id", "reference", "point" };
                case OpSpotSlope: return new string[0];
                default: return null;
            }
        }

        /// <summary>
        /// Which required fields are absent, in table order. An unknown operation
        /// answers null, same contract as the table itself.
        /// </summary>
        public static List<string> MissingFields(string op, Func<string, bool> hasField)
        {
            IReadOnlyList<string> required = RequiredFields(op);
            if (required == null) return null;
            var missing = new List<string>();
            foreach (string field in required)
                if (hasField == null || !hasField(field)) missing.Add(field);
            return missing;
        }

        // ---- which options an operation carries at all ----------------------

        /// <summary>The optional fields the dimension operations know between them.</summary>
        public static readonly string[] OptionFields =
        {
            "dimension_type_id", "prefix", "suffix", "above", "below", "value_override",
            "eq", "lock", "leader", "bend", "end", "expected_value", "expected_tolerance"
        };

        /// <summary>
        /// Whether an operation carries an option AT ALL - before any reference-count
        /// eligibility. The table follows the API, not taste: radial and diameter take
        /// no type because RadialDimension.Create has no type parameter, and the spots
        /// take no expected_value because a spot has no measured Dimension.Value to
        /// hold it against.
        /// </summary>
        public static bool AllowsOption(string op, string option)
        {
            switch (op)
            {
                case OpDimension:
                    return option == "dimension_type_id" || option == "prefix" || option == "suffix"
                        || option == "above" || option == "below" || option == "value_override"
                        || option == "eq" || option == "lock"
                        || option == "expected_value" || option == "expected_tolerance";
                case OpAngular:
                    return option == "dimension_type_id"
                        || option == "expected_value" || option == "expected_tolerance";
                case OpRadial:
                case OpDiameter:
                case OpArcLength:
                    return option == "expected_value" || option == "expected_tolerance";
                case OpSpotElevation:
                case OpSpotCoordinate:
                    return option == "leader" || option == "bend" || option == "end";
                default:
                    return false;
            }
        }

        /// <summary>
        /// The options a request offered that this operation cannot honour, each with
        /// the reason. Refusing beats ignoring: an option that is silently dropped is
        /// a request the caller believes was honoured.
        /// </summary>
        public static List<string> UnavailableOptions(string op, IEnumerable<string> offeredOptions)
        {
            var refused = new List<string>();
            if (offeredOptions == null) return refused;
            foreach (string option in offeredOptions)
            {
                if (AllowsOption(op, option)) continue;
                refused.Add("'" + option + "' is not an option of '" + op + "'" + WhyUnavailable(op, option));
            }
            return refused;
        }

        private static string WhyUnavailable(string op, string option)
        {
            if (option == "dimension_type_id" && (op == OpRadial || op == OpDiameter || op == OpArcLength))
                return ": the creating API takes no dimension type, so the document's default type applies " +
                       "and is bound into the plan instead.";
            if (option == "leader" && op != OpSpotElevation && op != OpSpotCoordinate)
                return ": only the spot operations take a leader at creation (Dimension has no Leader " +
                       "property to set afterwards).";
            if ((option == "expected_value" || option == "expected_tolerance")
                && (op == OpSpotElevation || op == OpSpotCoordinate))
                return ": a spot has no measured Dimension.Value to hold an expectation against.";
            return ".";
        }

        // ---- reference-list arithmetic --------------------------------------

        /// <summary>How many references an operation's `references` array takes.</summary>
        public static bool ReferenceCountBounds(string op, out int min, out int max)
        {
            switch (op)
            {
                case OpDimension: min = 2; max = 32; return true;
                case OpAngular: min = 2; max = 2; return true;
                case OpArcLength: min = 2; max = 2; return true;
                default: min = 0; max = 0; return false;
            }
        }

        /// <summary>
        /// Bounds, empty entries and EXACT duplicates, in one message a caller can act
        /// on, or null when the list is clean. Duplicates are compared as exact strings
        /// on purpose: two stable representations that differ in any character are two
        /// different references as far as this command can prove, and guessing that
        /// they "mean the same thing" is a guess reported as a fact.
        /// </summary>
        public static string ReferenceListError(string op, IReadOnlyList<string> stableReferences)
        {
            int min, max;
            if (!ReferenceCountBounds(op, out min, out max))
                return "'" + op + "' does not take a references array.";

            string bounds = min == max ? "exactly " + min : min + ".." + max;
            if (stableReferences == null)
                return op + " needs 'references': an array of " + bounds + " stable reference strings.";
            if (stableReferences.Count < min || stableReferences.Count > max)
                return op + " needs " + bounds + " references; " + stableReferences.Count + " were sent.";

            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < stableReferences.Count; i++)
            {
                string s = stableReferences[i];
                if (string.IsNullOrWhiteSpace(s))
                    return "references[" + i + "] is empty. Every entry must be a stable reference string " +
                           "(see horizun_get_dimension_references).";
                int at;
                if (firstSeen.TryGetValue(s, out at))
                    return "references[" + i + "] duplicates references[" + at + "] - the exact same stable " +
                           "representation twice. A dimension cannot hold one reference in two positions; " +
                           "remove the duplicate.";
                firstSeen[s] = i;
            }
            return null;
        }

        // ---- option eligibility by reference count --------------------------

        /// <summary>
        /// Which offered options this reference count makes ineligible. The single-
        /// segment overrides (prefix/suffix/above/below/value_override) and lock need
        /// EXACTLY two references - one segment to carry them; eq needs at least three
        /// - two segments to equalise. Answered per option so the message names each.
        /// </summary>
        public static List<string> IneligibleOptions(int referenceCount, IEnumerable<string> offeredOptions)
        {
            var refused = new List<string>();
            if (offeredOptions == null) return refused;
            foreach (string option in offeredOptions)
            {
                bool singleSegment = option == "prefix" || option == "suffix" || option == "above"
                                  || option == "below" || option == "value_override" || option == "lock";
                if (singleSegment && referenceCount != 2)
                    refused.Add("'" + option + "' needs exactly 2 references (one segment to carry it); " +
                                "this action has " + referenceCount + ".");
                else if (option == "eq" && referenceCount < 3)
                    refused.Add("'eq' needs at least 3 references (two segments to equalise); this action " +
                                "has " + referenceCount + ".");
            }
            return refused;
        }

        // ---- units ----------------------------------------------------------

        public const double MmPerFoot = 304.8;

        /// <summary>mm | m | feet, to internal feet. The same table every command uses.</summary>
        public static bool UnitScale(string units, out double toFeet)
        {
            switch (units)
            {
                case "feet": toFeet = 1.0; return true;
                case "m": toFeet = 1.0 / 0.3048; return true;
                case "mm": toFeet = 1.0 / MmPerFoot; return true;
                default: toFeet = 0.0; return false;
            }
        }

        /// <summary>The default expected_value tolerance: 0.1 mm, in internal feet.</summary>
        public const double DefaultExpectedToleranceFeet = 0.1 / MmPerFoot;

        /// <summary>
        /// The default expected_value tolerance for ANGULAR values: 0.01 degrees, in
        /// radians. Millimetres mean nothing to an angle, so the linear default cannot
        /// be borrowed; 0.01 degrees is comparable in spirit to 0.1 mm on a length.
        /// </summary>
        public static readonly double DefaultAngularToleranceRadians = DegreesToRadians(0.01);

        public static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        /// <summary>
        /// The tolerance an expected_value comparison runs with, in internal feet:
        /// the caller's value converted with the request's unit factor, or the 0.1 mm
        /// default when none was given.
        /// </summary>
        public static double ExpectedToleranceFeet(double? requestedInUnits, double unitToFeet)
            => requestedInUnits.HasValue ? requestedInUnits.Value * unitToFeet : DefaultExpectedToleranceFeet;

        // ---- canonical rounding and geometry fingerprints -------------------

        /// <summary>
        /// One internal-feet number as the plan renders it: millimetres, rounded to
        /// 0.1 mm, invariant culture. Rounded because Revit's regeneration jitters the
        /// last floating-point digits, and a fingerprint that changes on its own would
        /// refuse every apply; 0.1 mm because anything a person would call "the face
        /// moved" is far larger.
        /// </summary>
        public static string CanonicalFeet(double feet)
        {
            double mm = Math.Round(feet * MmPerFoot, 1, MidpointRounding.AwayFromZero);
            if (mm == 0d) mm = 0d;   // never render negative zero as a distinct value
            return mm.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>Three coordinates, canonically, comma-joined.</summary>
        public static string CanonicalPoint(double xFeet, double yFeet, double zFeet)
            => CanonicalFeet(xFeet) + "," + CanonicalFeet(yFeet) + "," + CanonicalFeet(zFeet);

        /// <summary>
        /// The fingerprint of ONE reference's geometric facts. The order index is part
        /// of it on purpose: a dimension's references are positional, and the same two
        /// references swapped are a different dimension. Separators are control
        /// characters so no fact can forge a boundary.
        /// </summary>
        public static string GeometryFingerprint(int order, string referenceType, string kind,
                                                 IEnumerable<double> feetFacts)
        {
            var sb = new StringBuilder();
            sb.Append(order.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            sb.Append(referenceType ?? "").Append((char)31);
            sb.Append(kind ?? "").Append((char)31);
            if (feetFacts != null)
                foreach (double fact in feetFacts)
                    sb.Append(CanonicalFeet(fact)).Append((char)30);
            return Sha256Hex(sb.ToString());
        }

        /// <summary>One fingerprint over many, ORDER-SENSITIVE - positions are the plan.</summary>
        public static string CombineFingerprints(IEnumerable<string> fingerprints)
        {
            var sb = new StringBuilder();
            if (fingerprints != null)
                foreach (string f in fingerprints)
                    sb.Append(f ?? "").Append((char)31);
            return Sha256Hex(sb.ToString());
        }

        private static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var hex = new StringBuilder(h.Length * 2);
                foreach (byte b in h) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        // ---- curve comparison, with the tolerance NAMED ---------------------

        /// <summary>
        /// The tolerance every re-read curve comparison runs at, in feet, published in
        /// the response as comparison_tolerance_feet so "the same line" is a claim with
        /// a number on it rather than an adjective.
        /// </summary>
        public const double CurveToleranceFeet = 1e-6;

        /// <summary>Two 3D points, within a Euclidean tolerance. Malformed input is false, never a pass.</summary>
        public static bool SamePoint(IReadOnlyList<double> a, IReadOnlyList<double> b, double toleranceFeet)
        {
            if (a == null || b == null || a.Count != 3 || b.Count != 3) return false;
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return !double.IsNaN(d) && d <= toleranceFeet;
        }

        /// <summary>
        /// The same segment, in either direction: Revit is free to hand a curve back
        /// with its endpoints swapped, and that is the same committed geometry.
        /// </summary>
        public static bool SameEndpoints(IReadOnlyList<double> aStart, IReadOnlyList<double> aEnd,
                                         IReadOnlyList<double> bStart, IReadOnlyList<double> bEnd,
                                         double toleranceFeet)
            => (SamePoint(aStart, bStart, toleranceFeet) && SamePoint(aEnd, bEnd, toleranceFeet))
            || (SamePoint(aStart, bEnd, toleranceFeet) && SamePoint(aEnd, bStart, toleranceFeet));

        /// <summary>
        /// Distance from a point to the INFINITE line through origin along direction.
        /// NaN when the direction is degenerate - the caller must fail closed on it,
        /// because a distance nobody could compute is not a small one.
        /// </summary>
        public static double DistancePointToLine(IReadOnlyList<double> origin, IReadOnlyList<double> direction,
                                                 IReadOnlyList<double> point)
        {
            if (origin == null || direction == null || point == null
                || origin.Count != 3 || direction.Count != 3 || point.Count != 3) return double.NaN;
            double len = Math.Sqrt(direction[0] * direction[0] + direction[1] * direction[1]
                                 + direction[2] * direction[2]);
            if (double.IsNaN(len) || len < 1e-12) return double.NaN;
            double ux = direction[0] / len, uy = direction[1] / len, uz = direction[2] / len;
            double vx = point[0] - origin[0], vy = point[1] - origin[1], vz = point[2] - origin[2];
            double cx = vy * uz - vz * uy, cy = vz * ux - vx * uz, cz = vx * uy - vy * ux;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        /// <summary>On the infinite line, within tolerance. NaN answers false - fail closed.</summary>
        public static bool PointOnLine(IReadOnlyList<double> origin, IReadOnlyList<double> direction,
                                       IReadOnlyList<double> point, double toleranceFeet)
        {
            double d = DistancePointToLine(origin, direction, point);
            return !double.IsNaN(d) && d <= toleranceFeet;
        }

        // ---- segment arithmetic ---------------------------------------------

        /// <summary>
        /// What Dimension.NumberOfSegments reads for a dimension over N references:
        /// Revit counts a two-reference dimension as ZERO segments (the value lives on
        /// the dimension itself), and a chain of N as N-1.
        /// </summary>
        public static int ExpectedSegmentCount(int referenceCount)
            => referenceCount <= 2 ? 0 : referenceCount - 1;

        /// <summary>
        /// One total from Revit's two shapes of answer: the dimension's own Value when
        /// it has one (single segment), the sum of segment values otherwise, and null
        /// when neither exists - which is "not measured", never zero.
        /// </summary>
        public static double? TotalOf(double? singleValue, IReadOnlyList<double> segmentValues)
        {
            if (singleValue.HasValue) return singleValue;
            if (segmentValues == null || segmentValues.Count == 0) return null;
            double total = 0;
            foreach (double v in segmentValues) total += v;
            return total;
        }

        /// <summary>
        /// The OWNER half of each stable representation, sorted. Revit canonicalises a
        /// reference on storage - a bare element reference to a grid handed to
        /// NewDimension reads back as 'uid:0:SURFACE' (measured live on 2025) - so a
        /// request-vs-read comparison of raw strings refuses correct dimensions. The
        /// owner prefix (everything before the first ':') survives canonicalisation;
        /// two references with the same owners in the same multiset are the same set
        /// of elements being measured.
        /// </summary>
        public static List<string> ReferenceOwnerKeys(IEnumerable<string> stableRepresentations)
        {
            var keys = new List<string>();
            if (stableRepresentations == null) return keys;
            foreach (string rep in stableRepresentations)
            {
                if (rep == null) { keys.Add(""); continue; }
                int colon = rep.IndexOf(':');
                keys.Add(colon < 0 ? rep : rep.Substring(0, colon));
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        // ---- the final state, over Revit's REAL statuses --------------------

        public const string StateCommittedVerified = "committed_verified";
        public const string StateRolledBack = "rolled_back";
        public const string StateRefused = "refused";
        public const string StateStalePlan = "stale_plan";
        public const string StateUncertain = "uncertain";

        /// <summary>
        /// The one word the response publishes about an apply, decided from what Revit
        /// ACTUALLY returned:
        ///
        ///   * committed_verified - the commit was confirmed, every postcondition was
        ///     re-read and matched, and no rollback was ever attempted;
        ///   * rolled_back - every attempted rollback answered RolledBack, so nothing
        ///     from this call is in the model;
        ///   * uncertain - everything else: a rollback that answered Pending or Error
        ///     or was never attempted after a failure, or a "verified" claim standing
        ///     next to a rollback. Uncertainty is kept, never smoothed - the caller
        ///     must re-read the model instead of trusting a word.
        ///
        /// refused and stale_plan are decided BEFORE any write by the confirmation
        /// machinery, so they never reach this function.
        /// </summary>
        public static string FinalState(bool applyVerified, IReadOnlyList<string> rollbackStatuses)
        {
            bool anyRollback = rollbackStatuses != null && rollbackStatuses.Count > 0;
            if (applyVerified)
                return anyRollback ? StateUncertain : StateCommittedVerified;
            if (!anyRollback) return StateUncertain;
            foreach (string status in rollbackStatuses)
                if (!PlanFailure.IsConfirmedRollback(status)) return StateUncertain;
            return StateRolledBack;
        }

        // ---- refusals for APIs that do not exist ----------------------------
        //
        // These texts travel as ORDINARY argument errors, never as
        // UnsupportedCapability: that type is the machine-readable grant of the
        // Python fallback, and Python cannot call a class that is absent from the
        // loaded RevitAPI.dll any more than the typed path can. A grant here would
        // send a client to write a script against an API that is not there.

        /// <summary>An operation whose creating API arrives in a later Revit than this one.</summary>
        public static string NoApiThisYear(string operation, string api, int introducedIn, string hostYear)
            => "'" + operation + "' is not supported on Revit " + hostYear + ": " + api +
               " exists only from Revit " + introducedIn + ". The Python fallback cannot supply it either - " +
               "the class is absent from this Revit's API, so a script would fail the same way, and no " +
               "fallback is offered. Nothing was written. Open the model in Revit " + introducedIn +
               " or newer to place this dimension.";

        /// <summary>spot_slope: no creation API in ANY supported Revit.</summary>
        public static string NoApiAnyYear(string operation)
            => "'" + operation + "' is not supported on any Revit this bridge runs on: no Revit API can " +
               "create a spot slope dimension (2023-2027 all lack a creation route). The Python fallback " +
               "cannot supply it either - a script would call the same absent API, so no fallback is " +
               "offered. Nothing was written. Place spot slopes by hand in Revit.";

        /// <summary>
        /// Whether the text Revit stored IS the text the caller asked for, once Revit's own
        /// line-ending re-encoding is undone.
        ///
        /// MEASURED on Revit 2023 (2026-08-24): TextNote.Create with the literal
        /// 'D7_PROBE' reads back as 'D7_PROBE\r' - Revit terminates the note with a
        /// carriage return the caller never sent, and it stores line SEPARATORS as '\r'
        /// whatever arrived. A strict == therefore refused every correct text note the
        /// bridge ever created, which is a false negative: the verification rejected work
        /// that was right.
        ///
        /// The normalisation is exactly the re-encoding Revit performs and NOTHING more:
        /// every line-ending form becomes '\n', and trailing newlines are dropped.
        /// Substance - the characters of every line, including interior blank lines and
        /// leading/trailing spaces WITHIN a line - still has to match exactly.
        /// </summary>
        public static bool StoredTextMatches(string requested, string stored)
        {
            if (requested == null || stored == null) return false;
            return NormalizeRevitText(requested) == NormalizeRevitText(stored);
        }

        private static string NormalizeRevitText(string s)
        {
            return s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        }
    }
}
