// -----------------------------------------------------------------------------
// Horizun Revit MCP - a declared hook length against the one the model carries.
// Original Horizun code. No Revit types: the model's side arrives as the row
// RebarHookLengths.Read emits, built by ReadRow below, so the plan, the apply's
// re-read and the audit make one comparison and it can be proved at a desk.
//
// A HOOK LENGTH IS A TYPE PROPERTY. Revit keeps the straight extension of a hook
// on the BAR TYPE, once per hook type (RebarBarType.GetHookLength/SetHookLength,
// with an auto-calculate switch per pair). Writing it changes every bar of that
// type that carries that hook - bars this set never built included. That is why
// it is never inferred, why the plan names every pair it would change, and why
// the reply says so in words rather than leaving it to be noticed.
//
// AUTO-CALCULATED IS NOT THE DECLARED LENGTH even when the numbers agree today:
// Revit recomputes it from the bend diameter, so it follows a type edit nobody
// connected to the hook. A declared length is a fixed one; anything else is a
// change in the plan, a failed re-read in the apply and a finding in the audit.
// -----------------------------------------------------------------------------
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class HookLengthRules
    {
        public const string TypeChangeMeans =
            "this changes a TYPE, not a bar: the hook length is stored on the bar type, once per hook type, and " +
            "every bar of that type with that hook - in this set or not, built before or after - takes it. The " +
            "auto-calculated length is switched off for this pair and the declared one is written.";

        /// <summary>
        /// The model's side of one (bar type, hook type) pair, exactly as the Revit reader emits it.
        /// readable is false when either half could not be read - never a guess.
        /// </summary>
        public static JObject ReadRow(bool? autoCalculated, double? lengthMm, string why)
        {
            bool readable = autoCalculated.HasValue && lengthMm.HasValue && IsFinite(lengthMm.Value);
            return new JObject
            {
                ["auto_calculated"] = autoCalculated.HasValue ? (JToken)autoCalculated.Value : JValue.CreateNull(),
                ["length_mm"] = lengthMm.HasValue && IsFinite(lengthMm.Value) ? (JToken)Math.Round(lengthMm.Value, 3) : JValue.CreateNull(),
                ["readable"] = readable,
                ["why"] = why
            };
        }

        /// <summary>Is the pair as read the declared length, fixed? Unread is never "yes".</summary>
        public static bool Matches(JObject read, double declaredMm, double toleranceMm)
        {
            if (read == null || (bool?)read["readable"] != true) return false;
            if ((bool?)read["auto_calculated"] != false) return false;
            double? mm = read.Value<double?>("length_mm");
            return mm.HasValue && Math.Abs(mm.Value - declaredMm) <= toleranceMm;
        }

        private static JObject Pair(DeclaredHookLength d, long barTypeElementId, long hookTypeElementId)
        {
            return new JObject
            {
                ["path"] = d.Path,
                ["bar_type"] = new JObject
                {
                    ["id"] = d.BarTypeId,
                    ["type_name"] = d.BarTypeName,
                    ["element_id"] = barTypeElementId
                },
                ["hook_type"] = new JObject
                {
                    ["id"] = d.HookTypeId,
                    ["type_name"] = d.HookTypeName,
                    ["element_id"] = hookTypeElementId
                },
                ["declared_mm"] = Math.Round(d.LengthMm, 3)
            };
        }

        /// <summary>
        /// One entry of the plan's bar_type_changes: what the model has now, what was declared,
        /// and whether the apply would write it. A pair already fixed at the declared length is
        /// listed with change: false; one that could not be read is a change - the apply writes it.
        /// </summary>
        public static JObject PlanEntry(DeclaredHookLength d, long barTypeElementId, long hookTypeElementId,
                                        JObject read, double toleranceMm)
        {
            JObject e = Pair(d, barTypeElementId, hookTypeElementId);
            e["resolved"] = true;
            e["current"] = read == null ? ReadRow(null, null, "not read") : read.DeepClone();
            bool change = !Matches(read, d.LengthMm, toleranceMm);
            e["change"] = change;
            e["why"] = !change
                ? "the bar type already carries this hook at the declared length, fixed (within " + Mm(toleranceMm) + " mm)."
                : (bool?)read?["readable"] != true
                    ? "the current hook length could not be read, so it is written as declared."
                    : (bool?)read["auto_calculated"] == true
                        ? "the length is auto-calculated now (" + Mm(read.Value<double>("length_mm")) + " mm); it becomes " +
                          "the declared " + Mm(d.LengthMm) + " mm, fixed."
                        : "the bar type carries " + Mm(read.Value<double>("length_mm")) + " mm; it becomes " + Mm(d.LengthMm) + " mm.";
            e["means"] = TypeChangeMeans;
            return e;
        }

        /// <summary>A declared pair whose bar type or hook type names nothing (or two things) in the model.</summary>
        public static JObject Unresolved(DeclaredHookLength d, string code, string why)
        {
            JObject e = Pair(d, -1, -1);
            e["resolved"] = false;
            e["code"] = code;
            e["why"] = why;
            e["change"] = JValue.CreateNull();
            e["means"] = TypeChangeMeans;
            return e;
        }

        /// <summary>
        /// The apply's post-commit block for one pair: requested, read back, verified. Verified
        /// only when the pair reads back FIXED at the declared length.
        /// </summary>
        public static JObject VerifyEntry(DeclaredHookLength d, long barTypeElementId, long hookTypeElementId,
                                          JObject before, JObject after, bool written, double toleranceMm)
        {
            JObject e = Pair(d, barTypeElementId, hookTypeElementId);
            e["requested_mm"] = Math.Round(d.LengthMm, 3);
            e["written"] = written;
            e["before"] = before == null ? JValue.CreateNull() : before.DeepClone();
            e["read"] = after == null ? ReadRow(null, null, "not read") : after.DeepClone();
            bool ok = Matches(after, d.LengthMm, toleranceMm);
            e["verified"] = ok;
            e["tolerance_mm"] = toleranceMm;
            if (!ok)
                e["why_not"] = (bool?)after?["readable"] != true
                    ? "the pair could not be read back after the commit."
                    : (bool?)after["auto_calculated"] == true
                        ? "the pair reads back auto-calculated, so the declared length did not take."
                        : "the pair reads back " + Mm(after.Value<double>("length_mm")) + " mm, not " + Mm(d.LengthMm) + " mm.";
            e["means"] = TypeChangeMeans;
            return e;
        }

        /// <summary>
        /// The audit's comparison for one declared pair. A length that differs, or one Revit
        /// auto-calculates, is an error; one that could not be read is unknown, never a pass.
        /// </summary>
        public static JArray AuditFindings(DeclaredHookLength d, long barTypeElementId, long hookTypeElementId,
                                           JObject read, double toleranceMm)
        {
            var findings = new JArray();
            if (Matches(read, d.LengthMm, toleranceMm)) return findings;
            JObject f;
            if (read == null || (bool?)read["readable"] != true)
                f = RebarAuditRules.Unknown(RebarFinding.HookLengthDiffers, d.Path, -1, Math.Round(d.LengthMm, 3),
                    "the hook length of '" + d.HookTypeName + "' on bar type '" + d.BarTypeName + "' could not be read" +
                    (read != null && read["why"] != null && read["why"].Type != JTokenType.Null ? ": " + (string)read["why"] : "."));
            else
            {
                bool auto = (bool?)read["auto_calculated"] == true;
                double mm = read.Value<double>("length_mm");
                f = RebarAuditRules.Finding(RebarFinding.HookLengthDiffers, RebarSeverity.Error, d.Path, -1,
                    Math.Round(d.LengthMm, 3), mm, Mm(toleranceMm) + " mm",
                    auto
                        ? "bar type '" + d.BarTypeName + "' auto-calculates the length of hook '" + d.HookTypeName + "' (" +
                          Mm(mm) + " mm now). The set declares " + Mm(d.LengthMm) + " mm, fixed; an auto-calculated " +
                          "length follows the bend diameter and is not the declaration."
                        : "bar type '" + d.BarTypeName + "' carries hook '" + d.HookTypeName + "' at " + Mm(mm) +
                          " mm and the set declares " + Mm(d.LengthMm) + " mm. It is a TYPE property: every bar of " +
                          "that type with that hook has the model's length.",
                    true, "horizun_apply_reinforcement");
                f["auto_calculated"] = auto;
            }
            f["bar_type_element_id"] = barTypeElementId;
            f["hook_type_element_id"] = hookTypeElementId;
            f["bar_type"] = d.BarTypeId;
            f["hook_type"] = d.HookTypeId;
            findings.Add(f);
            return findings;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
