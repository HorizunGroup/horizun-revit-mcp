// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_set_keynote — replaces apply_keynote_to_element.
//
// Keynotes are how Horizun ties a model to a budget: the keynote code is the
// join key between an element and a line item. A keynote that silently did not
// land is a quantity that silently does not get billed, so this handler is built
// to be unable to claim a write it cannot prove.
//
// Three defects in the handler this replaces, all of which we hit for real:
//
//   1. THE BLAST RADIUS. In Revit the Keynote parameter lives on the TYPE, not
//      the instance — that is the normal case, not the edge case. The old
//      handler walked to the type, wrote there, and reported the ELEMENT id. So
//      "keynote applied to 1 door" quietly re-coded every door of that type in
//      the project. Nothing in the response hinted at it. We now resolve the
//      write target first, write each type ONCE, and report exactly which
//      elements were affected — including the ones the caller never mentioned.
//   2. `Parameter.Set()` returns a bool and the old code discarded it. Set() can
//      decline a write and return false without throwing. Reporting "updated"
//      off a call that returned false is the same class of lie as counting a
//      rolled-back Commit as success.
//   3. Nothing re-read the model afterwards. We now read the value back and
//      compare, so what we report is the model's state, not our intent.
//
// Also: ids that are not integers used to be dropped from the request without a
// word, and `requested` was counted after the drop, so the numbers looked
// consistent while the caller's elements were never touched. They are errors now.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunSetKeynoteHandler : IRevitCommand
    {
        public string Name => "horizun_set_keynote";

        public string Description =>
            "Set the Keynote code on elements, reporting exactly what it touched. In Revit the Keynote " +
            "parameter normally lives on the TYPE, so writing it re-codes every instance of that type: " +
            "this tool resolves the target first, tells you the blast radius (including elements you did " +
            "not name), writes each type once, and re-reads the model to confirm. Use scope='instance' to " +
            "refuse any write that would spill onto siblings, or dry_run=true to see the impact first.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""element_ids"", ""keynote""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1,
                       ""description"": ""Elements to code. If the Keynote lives on their type, the type is what gets written."" },
    ""keynote"": { ""type"": ""string"", ""description"": ""The keynote code. Empty string clears it."" },
    ""scope"": { ""type"": ""string"", ""enum"": [""auto"", ""instance"", ""type""], ""default"": ""auto"",
                 ""description"": ""auto: write wherever the parameter lives (type if that is the only place). instance: only write an instance-level Keynote; fail rather than spill onto siblings. type: always write the type."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Resolve targets and report the blast radius without writing."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            _census = null;   // the model may have changed since the last request

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var idsToken = request["element_ids"] as JArray;
            if (idsToken == null || idsToken.Count == 0)
                return CommandResult.Fail("element_ids is required and must not be empty.");

            var keynote = request.Value<string>("keynote");
            if (keynote == null)
                return CommandResult.Fail("keynote is required (use \"\" to clear it).");

            var scope = (request.Value<string>("scope") ?? "auto").ToLowerInvariant();
            if (scope != "auto" && scope != "instance" && scope != "type")
                return CommandResult.Fail("scope must be 'auto', 'instance' or 'type'.");
            bool dryRun = request.Value<bool>("dry_run");

            var failed = new JArray();
            var ids = new List<long>();
            foreach (var tok in idsToken)
            {
                // An id we cannot read is an error, not something to drop quietly.
                if (tok.Type != JTokenType.Integer)
                {
                    failed.Add(new JObject { ["element_id"] = tok.ToString(), ["error"] = "Not an integer element id; ignored ids are how a caller thinks it coded elements it never touched." });
                    continue;
                }
                ids.Add(tok.Value<long>());
            }

            // ---- Resolve where each write would land, before writing anything. ----
            var plans = new List<WritePlan>();
            foreach (var id in ids)
            {
                if (!RevitCompat.CanRepresentElementId(id))
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = RevitCompat.ElementIdRangeError(id) });
                    continue;
                }
                var elem = doc.GetElement(RevitCompat.ToElementId(id));
                if (elem == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = "Element not found." });
                    continue;
                }

                var plan = Resolve(doc, elem, scope, out string why);
                if (plan == null)
                {
                    failed.Add(new JObject { ["element_id"] = id, ["error"] = why });
                    continue;
                }
                plans.Add(plan);
            }

            // One type written once, no matter how many of its instances were named.
            var byTarget = plans
                .GroupBy(p => p.Target.Id.ToString())
                .Select(g => g.First())
                .ToList();

            // ---- Blast radius: who else changes because they share the type. ----
            var targets = new JArray();
            foreach (var plan in byTarget)
            {
                var requested = plans.Where(p => p.Target.Id == plan.Target.Id).Select(p => p.Source.Id).ToList();
                var affected = plan.IsTypeLevel ? InstancesOfType(doc, plan.Target.Id) : new List<ElementId> { plan.Source.Id };
                var collateral = affected.Where(a => !requested.Contains(a)).ToList();

                plan.Collateral = collateral.Count;

                targets.Add(new JObject
                {
                    ["writes_to"] = plan.IsTypeLevel ? "type" : "instance",
                    ["target_id"] = plan.Target.Id.ToString(),
                    ["target_name"] = SafeName(plan.Target),
                    ["parameter"] = plan.Parameter.Definition?.Name,
                    ["current_keynote"] = plan.Parameter.AsString() ?? "",
                    ["requested_elements"] = new JArray(requested.Select(r => (JToken)r.ToString())),
                    ["elements_affected"] = affected.Count,
                    ["collateral_elements"] = collateral.Count,
                    ["collateral_note"] = collateral.Count == 0
                        ? null
                        : $"{collateral.Count} element(s) you did not name share this type and WILL be re-coded. " +
                          "The Keynote lives on the type; there is no way to code one instance without them. " +
                          "Use scope='instance' to refuse this, or duplicate the type first."
                });
            }

            if (dryRun)
            {
                return CommandResult.Ok(new JObject
                {
                    ["dry_run"] = true,
                    ["keynote"] = keynote,
                    ["requested_ids"] = ids.Count,
                    ["writes_planned"] = byTarget.Count,
                    ["targets"] = targets,
                    ["failed"] = failed,
                    ["total_elements_affected"] = byTarget.Sum(p => p.IsTypeLevel ? InstancesOfType(doc, p.Target.Id).Count : 1),
                    ["note"] = "Nothing was written. Re-run with dry_run=false."
                });
            }

            // ---- Write. ----
            var written = new JArray();
            int confirmed = 0;
            using (var tx = new Transaction(doc, "Horizun: set keynote"))
            {
                tx.Start();
                try
                {
                    foreach (var plan in byTarget)
                    {
                        var before = plan.Parameter.AsString() ?? "";
                        bool accepted;
                        try { accepted = plan.Parameter.Set(keynote); }
                        catch (Exception ex)
                        {
                            failed.Add(new JObject { ["target_id"] = plan.Target.Id.ToString(), ["error"] = ex.Message });
                            continue;
                        }

                        // Set() returning false is a refused write that does not throw.
                        if (!accepted)
                        {
                            failed.Add(new JObject
                            {
                                ["target_id"] = plan.Target.Id.ToString(),
                                ["error"] = "Revit refused the write (Parameter.Set returned false). Nothing changed on this target."
                            });
                            continue;
                        }

                        // Read the model back. Intent is not evidence.
                        var after = plan.Parameter.AsString() ?? "";
                        bool ok = string.Equals(after, keynote, StringComparison.Ordinal);
                        if (ok) confirmed++;

                        written.Add(new JObject
                        {
                            ["writes_to"] = plan.IsTypeLevel ? "type" : "instance",
                            ["target_id"] = plan.Target.Id.ToString(),
                            ["target_name"] = SafeName(plan.Target),
                            ["before"] = before,
                            ["after"] = after,
                            ["confirmed"] = ok,
                            ["elements_affected"] = plan.IsTypeLevel ? InstancesOfType(doc, plan.Target.Id).Count : 1,
                            ["collateral_elements"] = plan.Collateral,
                            ["error"] = ok ? null : $"Set() was accepted but the model reads back '{after}', not '{keynote}'."
                        });
                    }

                    // Turns a silent rollback into an error instead of a false success.
                    HorizunGuard.Commit(tx, "set keynote");
                }
                catch (HorizunSilentRollbackException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    return CommandResult.Fail("Set keynote failed, nothing written: " + ex.Message);
                }
            }

            int totalAffected = written
                .Where(w => (bool)w["confirmed"])
                .Sum(w => (int)w["elements_affected"]);

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = false,
                ["keynote"] = keynote,
                ["requested_ids"] = ids.Count,
                ["writes_attempted"] = byTarget.Count,
                ["writes_confirmed"] = confirmed,
                ["elements_now_carrying_this_keynote"] = totalAffected,
                ["verification"] = JObject.FromObject(HorizunGuard.Verify("keynote writes", byTarget.Count, confirmed)),
                ["written"] = written,
                ["failed"] = failed
            });
        }

        private class WritePlan
        {
            public Element Source;      // what the caller named
            public Element Target;      // what actually gets written (may be the type)
            public Parameter Parameter;
            public bool IsTypeLevel;
            public int Collateral;
        }

        /// <summary>
        /// Decide where the write lands and say why if it cannot. Honest about the
        /// instance/type distinction rather than silently walking to the type.
        /// </summary>
        private static WritePlan Resolve(Document doc, Element elem, string scope, out string why)
        {
            why = null;

            Parameter instP = null;
            if (scope != "type")
            {
                instP = elem.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? elem.LookupParameter("Keynote");
                if (instP != null && instP.IsReadOnly) instP = null;
            }

            if (instP != null && Usable(instP, out why))
                return new WritePlan { Source = elem, Target = elem, Parameter = instP, IsTypeLevel = false };

            if (scope == "instance")
            {
                why = "No writable instance-level Keynote on this element. In Revit the Keynote normally " +
                      "lives on the type, and writing it there would re-code every sibling instance. " +
                      "scope='instance' refuses that. Use scope='auto' to accept the type-wide write, or " +
                      "duplicate the type so this element can carry its own code.";
                return null;
            }

            var typeId = elem.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
            {
                why = "Element has no type, and no writable instance-level Keynote parameter.";
                return null;
            }
            var type = doc.GetElement(typeId);
            if (type == null)
            {
                why = "Element's type could not be resolved.";
                return null;
            }

            var typeP = type.get_Parameter(BuiltInParameter.KEYNOTE_PARAM) ?? type.LookupParameter("Keynote");
            if (typeP == null)
            {
                why = "Neither the element nor its type has a Keynote parameter.";
                return null;
            }
            if (!Usable(typeP, out why)) return null;

            return new WritePlan { Source = elem, Target = type, Parameter = typeP, IsTypeLevel = true };
        }

        private static bool Usable(Parameter p, out string why)
        {
            why = null;
            if (p.IsReadOnly) { why = "Keynote parameter is read-only here."; return false; }
            if (p.StorageType != StorageType.String)
            {
                why = "Keynote parameter is " + p.StorageType + ", not a string; this is not the Revit Keynote field.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Every instance of a type — i.e. everyone a type-level write reaches.
        ///
        /// Deliberately NOT FamilyInstanceFilter: that only matches FamilyInstances,
        /// so for a wall/floor/roof type it returns empty WITHOUT throwing, and we
        /// would report a blast radius of zero for exactly the categories where a
        /// type-wide re-code does the most damage. One scan of the model, cached per
        /// request, covers every category and costs less than a filter per target.
        /// </summary>
        // Per-request only. The dispatcher keeps one handler instance alive for the
        // whole session, so a cache that outlived a request would answer the next
        // one from a model that has since changed. Cleared on entry to Execute.
        private Dictionary<ElementId, List<ElementId>> _census;

        private List<ElementId> InstancesOfType(Document doc, ElementId typeId)
        {
            if (_census == null)
            {
                _census = new Dictionary<ElementId, List<ElementId>>();
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    ElementId tid;
                    try { tid = e.GetTypeId(); } catch { continue; }
                    if (tid == null || tid == ElementId.InvalidElementId) continue;
                    if (!_census.TryGetValue(tid, out var list))
                        _census[tid] = list = new List<ElementId>();
                    list.Add(e.Id);
                }
            }
            return _census.TryGetValue(typeId, out var hits) ? hits : new List<ElementId>();
        }

        private static string SafeName(Element e)
        {
            try { return e?.Name; } catch { return null; }
        }
    }
}
