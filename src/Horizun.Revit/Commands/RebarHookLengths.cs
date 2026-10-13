// -----------------------------------------------------------------------------
// Horizun Revit MCP - the declared hook lengths, resolved, read and written.
// Original Horizun code.
//
// Revit keeps a hook's straight extension on the BAR TYPE, per hook type:
// RebarBarType.GetAutoCalcHookLengths / SetAutoCalcHookLengths and
// GetHookLength / SetHookLength, in feet. Read by reflection over the five
// installed RevitAPI.dll (2023-2027): the same members, with the same
// signatures, in every year - so this file has no #if. The comparison itself
// lives in Core/HookLengthRules, which the row below is built by.
//
// Shared by the plan, the apply and the audit, so the pair a person approved in
// the rehearsal is resolved the same way when it is written and when it is
// audited.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ResolvedHookLength
    {
        public DeclaredHookLength Declared;
        public RebarBarType BarType;
        public RebarHookType HookType;
        /// <summary>The pair as the model carries it when it was resolved (HookLengthRules.ReadRow).</summary>
        public JObject Before;
        public string Code;
        public string Why;
        public bool Ok { get { return Code == null; } }
        public long BarTypeElementId { get { return BarType == null ? -1 : Rid.Value(BarType.Id); } }
        public long HookTypeElementId { get { return HookType == null ? -1 : Rid.Value(HookType.Id); } }
    }

    public static class RebarHookLengths
    {
        public const double FtToMm = 304.8;

        /// <summary>Every declared pair, resolved by name and read. A name matching two types is refused, never a tie broken.</summary>
        public static List<ResolvedHookLength> Resolve(Document doc, StructuralRequirementSet set)
        {
            var rows = new List<ResolvedHookLength>();
            foreach (DeclaredHookLength d in set.DeclaredHookLengths())
            {
                var row = new ResolvedHookLength { Declared = d };
                int matches;
                row.BarType = ReinforcementResolver.ByName<RebarBarType>(doc, d.BarTypeName, out matches);
                if (row.BarType == null)
                {
                    row.Code = matches > 1 ? ReinforcementResolver.CodeBarTypeAmbiguous : ReinforcementResolver.CodeBarTypeNotFound;
                    row.Why = matches > 1
                        ? matches + " bar types are named '" + d.BarTypeName + "'. Which one's hook length is meant cannot be read from the set."
                        : "no RebarBarType in this document is named '" + d.BarTypeName + "', so there is no type to carry the hook length.";
                    rows.Add(row);
                    continue;
                }
                row.HookType = ReinforcementResolver.ByName<RebarHookType>(doc, d.HookTypeName, out matches);
                if (row.HookType == null)
                {
                    row.Code = matches > 1 ? ReinforcementResolver.CodeHookAmbiguous : ReinforcementResolver.CodeHookNotFound;
                    row.Why = matches > 1
                        ? matches + " hook types are named '" + d.HookTypeName + "'. Which one's length is meant cannot be read from the set."
                        : "no RebarHookType in this document is named '" + d.HookTypeName + "'.";
                    rows.Add(row);
                    continue;
                }
                row.Before = Read(row.BarType, row.HookType.Id);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>The pair as the model carries it now. Each half is read on its own, and a half that raises is null - never a guess.</summary>
        public static JObject Read(RebarBarType barType, ElementId hookId)
        {
            bool? auto = null;
            double? mm = null;
            var why = new List<string>();
            try { auto = barType.GetAutoCalcHookLengths(hookId); }
            catch (Exception ex) { why.Add("GetAutoCalcHookLengths raised: " + ex.Message); }
            try { mm = barType.GetHookLength(hookId) * FtToMm; }
            catch (Exception ex) { why.Add("GetHookLength raised: " + ex.Message); }
            return HookLengthRules.ReadRow(auto, mm, why.Count == 0 ? null : string.Join(" ", why));
        }

        /// <summary>
        /// Switch the pair's auto-calculation off and write the declared length. Inside the caller's
        /// transaction; a raise aborts the whole batch there.
        /// </summary>
        public static void Write(ResolvedHookLength row)
        {
            ElementId hookId = row.HookType.Id;
            row.BarType.SetAutoCalcHookLengths(hookId, false);
            row.BarType.SetHookLength(hookId, row.Declared.LengthMm / FtToMm);
        }

        /// <summary>The plan's bar_type_changes entry for one resolved or refused pair.</summary>
        public static JObject Describe(ResolvedHookLength row, double toleranceMm)
        {
            return row.Ok
                ? HookLengthRules.PlanEntry(row.Declared, row.BarTypeElementId, row.HookTypeElementId, row.Before, toleranceMm)
                : HookLengthRules.Unresolved(row.Declared, row.Code, row.Why);
        }

        public static bool NeedsChange(ResolvedHookLength row, double toleranceMm)
        {
            return row.Ok && !HookLengthRules.Matches(row.Before, row.Declared.LengthMm, toleranceMm);
        }

        /// <summary>What the token depends on: a pair edited between rehearsal and apply invalidates it.</summary>
        public static Dictionary<string, string> BeforeValues(ResolvedHookLength row, string setSha)
        {
            JObject b = row.Before ?? new JObject();
            return new Dictionary<string, string>
            {
                { "path", row.Declared.Path },
                { "set_sha", setSha },
                { "bar_type_uid", SafeUid(row.BarType) },
                { "hook_type_uid", SafeUid(row.HookType) },
                { "auto_calculated", b["auto_calculated"] == null || b["auto_calculated"].Type == JTokenType.Null ? "(unread)" : ((bool)b["auto_calculated"] ? "true" : "false") },
                { "length_mm", b["length_mm"] == null || b["length_mm"].Type == JTokenType.Null ? "(unread)" : ((double)b["length_mm"]).ToString("0.###", CultureInfo.InvariantCulture) },
                { "declared_mm", row.Declared.LengthMm.ToString("0.###", CultureInfo.InvariantCulture) }
            };
        }

        private static string SafeUid(Element e)
        {
            try { return e == null ? null : e.UniqueId; } catch { return null; }
        }
    }
}
