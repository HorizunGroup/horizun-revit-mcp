// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The pre-delivery gate: a REQUIREMENT SET the caller declares, evaluated over
// what the audit MEASURED. Nothing is compiled in - the standard arrives as an
// argument - and the arithmetic honors what a measurement is:
//
//   * A LOWER BOUND CAN FAIL A LIMIT, NEVER PASS ONE. A check with incomplete
//     coverage that already counts past the limit has failed provably; the
//     same check under the limit proves nothing, and the row says so.
//   * AN UNKNOWN REQUIREMENT REFUSES THE WHOLE GATE. A misspelled requirement
//     silently ignored reads exactly like a requirement that passed.
//   * A WAIVED REQUIREMENT IS RECORDED, not deleted: forbid_x=false is the
//     decision to not require x, and the report keeps that decision visible.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    public sealed class GateMeasurement
    {
        public string Check;
        public double? Count;
        public bool Ran;
        public bool CoverageComplete;

        /// <summary>
        /// NAMED SUB-COUNTS, so one finding can answer more than one requirement.
        ///
        /// The gate used to read exactly one number per finding, which meant two
        /// requirements about links - how many are not loaded, and whether any
        /// path points inside a user profile - could not both be expressed: the
        /// `links` finding emits one count and the second requirement had nowhere
        /// to read from. A part is a full measurement in its own right, so it
        /// carries its own coverage and the lower-bound arithmetic applies to it
        /// unchanged.
        /// </summary>
        public Dictionary<string, GateMeasurement> Parts;

        /// <summary>
        /// PER-ITEM RESULTS, for a requirement that names things rather than
        /// counts them. "These five parameters must be present" is not a limit on
        /// a number; it is five questions, and a reader needs to know WHICH one
        /// failed. A count of failures cannot say that.
        /// </summary>
        public Dictionary<string, GateItemMeasurement> Items;
    }

    /// <summary>One named thing a list requirement asks about.</summary>
    public sealed class GateItemMeasurement
    {
        public string Name;
        /// <summary>null means it could not be read - which is not a failure and not a pass.</summary>
        public bool? Satisfied;
        public string Detail;
    }

    public sealed class GateRow
    {
        public string Requirement;
        public string Check;
        /// <summary>The item this row is about, for a list requirement. Null for a scalar one.</summary>
        public string Item;
        public double? Limit;
        public double? Measured;
        public string Status;      // pass | fail | not_measurable | waived
        public string Reason;
    }

    public static class PreDeliveryGateRules
    {
        public const string StatusPass = "pass";
        public const string StatusFail = "fail";
        public const string StatusNotMeasurable = "not_measurable";
        public const string StatusWaived = "waived";

        public const string VerdictPass = "pass";
        public const string VerdictFail = "fail";
        public const string VerdictNotAssessable = "not_assessable";

        /// <summary>requirement name -> (the check it reads, the limit for a forbid_).</summary>
        // EVERY TARGET IS AN AuditCheckNames CONSTANT, and a test asserts that
        // each one is a name the audit's findings actually carry. This map used to
        // spell them as literals, and one was wrong - forbid_orphan_group_types
        // pointed at "group_types" while the finding emits "orphan_group_types",
        // so that requirement was permanently not_measurable and a set containing
        // it could never return the verdict pass.
        private static readonly Dictionary<string, string> KnownMax = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "max_warnings", AuditCheckNames.Warnings },
            { "max_in_place_families", AuditCheckNames.InPlaceFamilies },
            { "max_views_off_sheets", AuditCheckNames.ViewsOffSheets },
            { "max_file_mb", AuditCheckNames.FileSizeMb },
            { "max_open_mep_connectors", AuditCheckNames.OpenMepConnectors },
            { "max_unpinned_links", AuditCheckNames.UnpinnedLinks },
            { "max_views_without_template", AuditCheckNames.ViewsWithoutTemplate },

            // THE DIAGNOSTICS P0 SLICE. Each names a PART of a finding, which is
            // what E1 added: one finding, several counts, so two questions about
            // one area do not need two findings.
            { "max_elements_far_from_origin", AuditCheckNames.Coordinates + "." + CoordinateCheckParts.ElementsFarFromOrigin },
            { "max_links_reflected", AuditCheckNames.Coordinates + "." + CoordinateCheckParts.LinksReflected },
            { "max_links_rotated", AuditCheckNames.Coordinates + "." + CoordinateCheckParts.LinksRotated },
            { "max_links_not_sharing_position", AuditCheckNames.Coordinates + "." + CoordinateCheckParts.LinksNotSharingPosition },
            { "max_duplicate_level_names", AuditCheckNames.Datums + "." + DatumCheckParts.DuplicateLevelNames },
            { "max_coincident_levels", AuditCheckNames.Datums + "." + DatumCheckParts.CoincidentLevels },
            { "max_levels_without_views", AuditCheckNames.Datums + "." + DatumCheckParts.LevelsWithoutViews },
            { "max_levels_without_elements", AuditCheckNames.Datums + "." + DatumCheckParts.LevelsWithoutElements },
            { "max_duplicate_grid_names", AuditCheckNames.Datums + "." + DatumCheckParts.DuplicateGridNames },
            { "max_coincident_grids", AuditCheckNames.Datums + "." + DatumCheckParts.CoincidentGrids },
            { "max_grids_off_axis", AuditCheckNames.Datums + "." + DatumCheckParts.GridsOffAxis }
        };
        private static readonly Dictionary<string, string> KnownForbid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "forbid_orphan_group_types", AuditCheckNames.OrphanGroupTypes },
            { "forbid_imported_cad", AuditCheckNames.ImportedCad },
            { "forbid_room_problems", AuditCheckNames.Rooms }
        };

        /// <summary>
        /// LIST REQUIREMENTS: the caller names the things, the gate answers one row
        /// each. The value maps onto a finding whose Items bag carries the per-item
        /// results - see GateMeasurement.Items for why a count will not do.
        /// </summary>
        private static readonly Dictionary<string, string> KnownRequire = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "require_coordinate_facts", AuditCheckNames.Coordinates + "." + CoordinateCheckParts.ControlPoints },
            { "require_4d_roles", AuditCheckNames.Readiness + ".4d" },
            { "require_5d_roles", AuditCheckNames.Readiness + ".5d" }
        };

        /// <summary>
        /// TOLERANCES CONFIGURE A CHECK; THEY DO NOT ASSERT ON IT.
        ///
        /// "Two levels within 1 mm are coincident" is not a requirement that can
        /// pass or fail - it is the number the coincidence check uses. Passed
        /// inside requirement_set it is neither a max_ nor a forbid_, so the gate
        /// refuses the whole call; registered as a requirement it would emit a
        /// meaningless pass row for a configuration value. It lives in a sibling
        /// object instead, exactly as horizun.structural-requirements/1 keeps its
        /// tolerances apart from its rules.
        /// </summary>
        private static readonly Dictionary<string, string> KnownTolerances = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { CoordinateRules.ToleranceFarRadius, AuditCheckNames.Coordinates },
            { CoordinateRules.ToleranceLinkOriginOffset, AuditCheckNames.Coordinates },
            { DatumRules.ToleranceLevelCoincidence, AuditCheckNames.Datums },
            { DatumRules.ToleranceGridCoincidence, AuditCheckNames.Datums },
            { DatumRules.ToleranceGridAxis, AuditCheckNames.Datums }
        };

        /// <summary>Every check name this gate maps a requirement onto. For the test.</summary>
        public static IEnumerable<string> MappedCheckNames()
        {
            foreach (string v in KnownMax.Values) yield return v;
            foreach (string v in KnownForbid.Values) yield return v;
            foreach (string v in KnownRequire.Values) yield return v;
        }

        /// <summary>Every tolerance this gate accepts in the sibling object, with what it configures.</summary>
        public static IEnumerable<KeyValuePair<string, string>> KnownToleranceNames()
        {
            foreach (KeyValuePair<string, string> kv in KnownTolerances) yield return kv;
        }

        /// <summary>
        /// Register a list requirement. Kept as a method rather than a literal so a
        /// story adding one cannot forget the name-agreement test: the target must
        /// be a check the audit can emit, and AuditCheckNameTests holds it to that.
        /// </summary>
        public static void RegisterRequire(string requirement, string check)
        {
            if (string.IsNullOrWhiteSpace(requirement)) throw new ArgumentException("requirement");
            if (string.IsNullOrWhiteSpace(check)) throw new ArgumentException("check");
            KnownRequire[requirement] = check;
        }

        public static void RegisterTolerance(string name, string configures)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
            KnownTolerances[name] = configures ?? "";
        }

        /// <summary>
        /// A CHECK NAME MAY NAME A PART OF A FINDING: "links.not_loaded" reads the
        /// `not_loaded` part of the `links` finding. A bare name reads the finding
        /// itself, exactly as before.
        /// </summary>
        private static bool TryResolve(IDictionary<string, GateMeasurement> measurements, string check,
                                       out GateMeasurement measurement)
        {
            measurement = null;
            if (measurements == null || string.IsNullOrEmpty(check)) return false;

            int dot = check.IndexOf('.');
            if (dot < 0) return measurements.TryGetValue(check, out measurement);

            string head = check.Substring(0, dot);
            string tail = check.Substring(dot + 1);
            GateMeasurement parent;
            if (!measurements.TryGetValue(head, out parent) || parent == null || parent.Parts == null) return false;
            return parent.Parts.TryGetValue(tail, out measurement);
        }

        /// <summary>
        /// One row per named item. A name the measurement does not carry is
        /// not_measurable naming that name - never a pass, because a requirement
        /// nobody measured reads exactly like one that was met.
        /// </summary>
        private static void EvaluateRequire(List<GateRow> rows, string name, string check,
                                            IEnumerable<string> wanted,
                                            IDictionary<string, GateMeasurement> measurements)
        {
            GateMeasurement measurement;
            bool haveMeasurement = TryResolve(measurements, check, out measurement) &&
                                   measurement != null && measurement.Ran;

            foreach (string item in wanted)
            {
                var row = new GateRow { Requirement = name, Check = check, Item = item };
                if (!haveMeasurement || measurement.Items == null)
                {
                    row.Status = StatusNotMeasurable;
                    row.Reason = "the '" + check + "' check produced no per-item results this run, so '" + item +
                                 "' was not measured. A gate cannot pass on a measurement that never happened.";
                    rows.Add(row);
                    continue;
                }

                GateItemMeasurement got;
                if (!measurement.Items.TryGetValue(item, out got) || got == null || got.Satisfied == null)
                {
                    row.Status = StatusNotMeasurable;
                    row.Reason = "'" + item + "' was not readable this run" +
                                 (got != null && !string.IsNullOrEmpty(got.Detail) ? ": " + got.Detail : ".") +
                                 " Unreadable is not the same as absent, and neither is a pass.";
                    rows.Add(row);
                    continue;
                }

                if (got.Satisfied.Value)
                {
                    row.Status = StatusPass;
                    row.Reason = "'" + item + "' is present and satisfies the requirement" +
                                 (string.IsNullOrEmpty(got.Detail) ? "." : ": " + got.Detail);
                }
                else
                {
                    row.Status = StatusFail;
                    row.Reason = "'" + item + "' does not satisfy the requirement" +
                                 (string.IsNullOrEmpty(got.Detail) ? "." : ": " + got.Detail);
                }
                rows.Add(row);
            }
        }

        public static IEnumerable<string> KnownRequirements()
        {
            foreach (string name in KnownMax.Keys) yield return name;
            foreach (string name in KnownForbid.Keys) yield return name;
            foreach (string name in KnownRequire.Keys) yield return name;
        }

        /// <summary>
        /// Evaluate the declared set over the measurements. A non-null return is the
        /// refusal message (unknown requirement, invalid value) and nothing else is
        /// produced - the gate answers whole or not at all.
        /// </summary>
        public static string Evaluate(IEnumerable<KeyValuePair<string, object>> requirementSet,
                                      IDictionary<string, GateMeasurement> measurements,
                                      out List<GateRow> rows, out string verdict)
        {
            rows = new List<GateRow>();
            verdict = null;
            var set = new List<KeyValuePair<string, object>>(requirementSet ?? new List<KeyValuePair<string, object>>());
            if (set.Count == 0) return "requirement_set must declare at least one requirement. Known: " +
                                       string.Join(", ", KnownRequirements()) + ".";
            foreach (KeyValuePair<string, object> requirement in set)
            {
                string name = requirement.Key;
                if (KnownMax.ContainsKey(name))
                {
                    double limit;
                    if (!TryNumber(requirement.Value, out limit) || limit < 0)
                        { rows.Clear(); return "requirement '" + name + "' needs a non-negative number, got '" +
                               (requirement.Value ?? "null") + "'."; }
                    rows.Add(EvaluateMax(name, KnownMax[name], limit, measurements));
                }
                else if (KnownForbid.ContainsKey(name))
                {
                    if (!(requirement.Value is bool required))
                        { rows.Clear(); return "requirement '" + name + "' needs true (enforce) or false (waive, recorded), got '" +
                               (requirement.Value ?? "null") + "'."; }
                    if (!required)
                        rows.Add(new GateRow
                        {
                            Requirement = name, Check = KnownForbid[name], Status = StatusWaived,
                            Reason = "declared false: the requirement is waived by the caller, and the waiver is recorded."
                        });
                    else rows.Add(EvaluateMax(name, KnownForbid[name], 0, measurements));
                }
                else if (KnownRequire.ContainsKey(name))
                {
                    List<string> wanted;
                    string bad = TryNameList(requirement.Value, out wanted);
                    if (bad != null) { rows.Clear(); return "requirement '" + name + "' " + bad; }
                    if (wanted.Count == 0)
                        rows.Add(new GateRow
                        {
                            Requirement = name, Check = KnownRequire[name], Status = StatusWaived,
                            Reason = "declared as an empty list: the requirement names nothing, and the decision " +
                                     "to require nothing is recorded rather than dropped."
                        });
                    else EvaluateRequire(rows, name, KnownRequire[name], wanted, measurements);
                }
                else if (KnownTolerances.ContainsKey(name))
                {
                    // A TOLERANCE IN THE REQUIREMENT SET IS STILL A REFUSAL. It is not
                    // a thing that can pass, and accepting it here to be helpful would
                    // put a configuration value in a compliance report.
                    rows.Clear();
                    return "'" + name + "' is a TOLERANCE, not a requirement: it configures the '" +
                           KnownTolerances[name] + "' check rather than asserting anything about it, so it " +
                           "cannot pass or fail. Pass it in the sibling 'tolerances' object instead.";
                }
                else
                {
                    // WHOLE OR NOT AT ALL, and that includes the rows. This header
                    // has always said "a non-null return is the refusal message and
                    // nothing else is produced"; it was not true - rows accumulated
                    // before the refusal survived in the out parameter. Every caller
                    // happens to discard them, so nothing was ever wrong in a reply,
                    // which is precisely how a contract and its implementation drift
                    // apart without anyone noticing.
                    rows.Clear();
                    return "requirement '" + name + "' is not one this gate measures. Known: " +
                           string.Join(", ", KnownRequirements()) + ". A misspelled requirement silently " +
                           "ignored would read exactly like one that passed, so the whole gate refuses.";
                }
            }

            bool anyFail = false, anyNotMeasurable = false;
            foreach (GateRow row in rows)
            {
                if (row.Status == StatusFail) anyFail = true;
                else if (row.Status == StatusNotMeasurable) anyNotMeasurable = true;
            }
            verdict = anyFail ? VerdictFail : (anyNotMeasurable ? VerdictNotAssessable : VerdictPass);
            return null;
        }

        /// <summary>
        /// Validate the sibling tolerances object. It produces NO rows: a tolerance
        /// is an input to a check, and a compliance report that listed its own
        /// configuration as passing would be counting its settings as achievements.
        /// A non-null return is the refusal message.
        /// </summary>
        public static string ValidateTolerances(IEnumerable<KeyValuePair<string, object>> tolerances)
        {
            if (tolerances == null) return null;
            foreach (KeyValuePair<string, object> t in tolerances)
            {
                if (!KnownTolerances.ContainsKey(t.Key))
                {
                    var known = new List<string>(KnownTolerances.Keys);
                    return "tolerance '" + t.Key + "' is not one this gate uses. Known: " +
                           (known.Count == 0 ? "(none registered)" : string.Join(", ", known)) +
                           ". A misspelled tolerance silently ignored would leave the check running on its " +
                           "default while the caller believed otherwise.";
                }
                double v;
                if (!TryNumber(t.Value, out v) || v < 0)
                    return "tolerance '" + t.Key + "' needs a non-negative number, got '" +
                           (t.Value ?? "null") + "'.";
            }
            return null;
        }

        /// <summary>A list of names, or the tail of a refusal message saying why not.</summary>
        private static string TryNameList(object value, out List<string> names)
        {
            names = new List<string>();
            var list = value as System.Collections.IEnumerable;
            if (value == null || value is string || list == null)
                return "needs a list of names, got '" + (value ?? "null") + "'. A single string is not a list: " +
                       "one name spelled as a bare value would silently become a one-item requirement.";
            foreach (object o in list)
            {
                string name = o as string;
                if (string.IsNullOrWhiteSpace(name))
                    return "contains an entry that is not a name: '" + (o ?? "null") + "'.";
                if (names.Contains(name))
                    return "names '" + name + "' twice. A duplicate would produce two rows about one thing.";
                names.Add(name);
            }
            return null;
        }

        private static GateRow EvaluateMax(string name, string check, double limit,
                                           IDictionary<string, GateMeasurement> measurements)
        {
            var row = new GateRow { Requirement = name, Check = check, Limit = limit };
            GateMeasurement measurement;
            if (!TryResolve(measurements, check, out measurement) || measurement == null || !measurement.Ran ||
                measurement.Count == null)
            {
                row.Status = StatusNotMeasurable;
                row.Reason = "the '" + check + "' check did not produce a count this run; a gate cannot pass on " +
                             "a measurement that never happened.";
                return row;
            }
            row.Measured = measurement.Count;
            double count = measurement.Count.Value;
            if (count > limit)
            {
                // Provable even from a lower bound: the real number is AT LEAST this.
                row.Status = StatusFail;
                row.Reason = Fmt(count) + " measured against a limit of " + Fmt(limit) +
                             (measurement.CoverageComplete ? "." : " - and coverage was incomplete, so the real number is at least this.");
                return row;
            }
            if (!measurement.CoverageComplete)
            {
                row.Status = StatusNotMeasurable;
                row.Reason = Fmt(count) + " is under the limit of " + Fmt(limit) + ", but it is a LOWER BOUND - " +
                             "the check could not read everything it examined, so being under the limit proves nothing.";
                return row;
            }
            row.Status = StatusPass;
            row.Reason = Fmt(count) + " against a limit of " + Fmt(limit) + ", with complete coverage.";
            return row;
        }

        private static bool TryNumber(object value, out double number)
        {
            switch (value)
            {
                case double d: number = d; return true;
                case int i: number = i; return true;
                case long l: number = l; return true;
                default: number = 0; return false;
            }
        }

        private static string Fmt(double value) =>
            value.ToString(value == Math.Floor(value) ? "0" : "0.##", CultureInfo.InvariantCulture);
    }
}
