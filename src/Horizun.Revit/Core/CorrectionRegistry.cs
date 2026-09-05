// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE EXPLICIT LIST OF CORRECTIONS THIS BRIDGE WILL PROPOSE, AND NOW RUN.
//
// This file is the safety model of the whole correction surface, and it is a
// list on purpose. A proposal may name a tool ONLY if that tool appears here,
// with its arguments assembled from typed fields and typed constants declared
// here - because the alternative is composing a tool name and an argument
// object out of a finding's message, and that is how a report becomes an
// arbitrary command. Validation downstream cannot repair it: by the time a
// well-formed call exists, nothing is left to notice that nobody chose it.
//
// EVERY FINDING TYPE THE AUDIT EMITS HAS AN ENTRY, AND MOST OF THEM SAY NO.
// That ratio is the honest one. Most audit findings are not mechanically
// correctable: they are a modelling decision, a missing input, or somebody
// else's file. An entry that refuses and SAYS WHY is worth more than one that
// guesses, because the reader can act on the reason - and an entry that is
// simply absent reads like an oversight rather than a decision.
//
// FIVE ENTRIES ACT, through typed commands that already exist and already
// verify their own work after the commit:
//
//   unpinned_links          -> horizun_manage_links   pin, one per link
//   links (unloaded)        -> horizun_manage_links   reload, one per type
//   views_without_template  -> horizun_manage_views   apply_template, template
//                              supplied by the caller as an input
//   orphan_group_types      -> horizun_delete_verified ids, DESTRUCTIVE
//   rooms (unplaced only)   -> horizun_delete_verified ids, DESTRUCTIVE
//
// The two deletions are offers a person selects finding by finding, rehearses,
// and confirms with a token. Nothing here deletes because an audit said so.
//
// horizun_execute_python is not here and will not be. A correction surface with
// an arbitrary-code escape hatch has no safety model at all - it has a list of
// suggestions and a way around the list.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CorrectionRegistry
    {
        public const string RegistryMeans =
            "a proposal may name a tool only if that tool appears in this registry, with its arguments " +
            "assembled from typed fields and typed constants declared beside it. A tool name composed out of " +
            "a finding's message is how a report becomes an arbitrary command, and validation downstream " +
            "cannot repair it: by then a well-formed call exists and nothing is left to notice nobody chose it.";

        public const string RefusalMeans =
            "most audit findings are not mechanically correctable - they are a modelling decision, a missing " +
            "input, or somebody else's file. An entry that refuses and says WHY is worth more than one that " +
            "guesses, because the reason is the part a reader can act on.";

        public const string DestructiveMeans =
            "a deletion is an OFFER, never an audit's decision: it runs only for a finding the caller named, " +
            "narrowed to the ids the caller listed, after a real rehearsal by horizun_delete_verified and a " +
            "confirmation token bound to exactly what the rehearsal resolved. It is not reversible from this " +
            "bridge - Revit's own undo is the only way back, and only inside the session. LISTING THE IDS IS " +
            "REQUIRED, not a convenience: an action on a destructive finding that omits element_ids is refused " +
            "as requires_input naming element_ids, because 'I did not say which' is not 'all of them'.";

        private static readonly Dictionary<string, CorrectionRecipe> _default =
            new Dictionary<string, CorrectionRecipe>(StringComparer.Ordinal)
            {
                // ACTIONABLE. A link that should be pinned is pinned by one typed
                // call, on one element, and pinning is reversible by unpinning.
                {
                    AuditCheckNames.UnpinnedLinks, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.UnpinnedLinks,
                        Tool = "horizun_manage_links",
                        FixedArguments = new JObject { ["operation"] = "pin" },
                        ElementArgument = "link_instance_id",
                        Risk = "low",
                        Reversible = true,
                        DryRunSupported = true,
                        ConfirmationRequired = true,
                        ExpectedOutcome = "the link instance is pinned, so it can no longer be moved by " +
                                          "accident. Nothing about the linked model changes.",
                        Verification = "horizun_manage_links re-reads the instance after the commit; the audit " +
                                       "check reports one fewer unpinned link."
                    }
                },

                // ACTIONABLE, FILTERED. The links finding lists every link type with
                // its status; only an UNLOADED one can be reloaded. A type whose file
                // is NotFound needs a path, and that is change_path with an input
                // nobody has supplied - it is excluded by its typed status, not by
                // reading the summary.
                {
                    AuditCheckNames.Links, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.Links,
                        Tool = "horizun_manage_links",
                        FixedArguments = new JObject { ["operation"] = "reload" },
                        ElementArgument = "link_type_id",
                        ItemFilterField = "status",
                        ItemFilterValues = new List<string> { "Unloaded", "LocallyUnloaded" },
                        // AN INVENTORY: this check lists every link type with its
                        // status, so a reloaded type is still listed and the
                        // re-audit has to read its status rather than look for it
                        // to disappear.
                        Postcondition = CorrectionPostcondition.ItemLeavesTheFilter,
                        Risk = "medium",
                        Reversible = true,
                        DryRunSupported = true,
                        ConfirmationRequired = true,
                        ExpectedOutcome = "the link type is reloaded from the path it already points at and " +
                                          "answers Loaded. A link type whose status is NotFound or Invalid is " +
                                          "NOT covered: it needs a new path, which is a decision.",
                        Verification = "horizun_manage_links re-reads GetLinkedFileStatus after the call; the " +
                                       "audit's links check reports one fewer unloaded type."
                    }
                },

                // REQUIRES INPUT, AND THE INPUT ANSWERS IT. Which template is a
                // decision about the project's standards, and this bridge compiles
                // none in - so the proposal returns the question, and once the caller
                // answers it with template_view_id the correction is actionable and
                // the second ambiguity travels as a caveat.
                {
                    AuditCheckNames.ViewsWithoutTemplate, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.ViewsWithoutTemplate,
                        Tool = "horizun_manage_views",
                        FixedArguments = new JObject { ["operation"] = "apply_template" },
                        ElementArgument = "view_id",
                        ActionsEnvelope = true,
                        RequiredArguments = new List<string> { "template_view_id" },
                        AmbiguitiesResolvedByInputs = true,
                        Risk = "medium",
                        Reversible = false,
                        DryRunSupported = true,
                        ConfirmationRequired = true,
                        Ambiguities = new List<string>
                        {
                            "WHICH template. A view without one is not a view with the wrong one, and this " +
                            "bridge carries no organisation's standards - the template is an argument, and " +
                            "nobody supplied it.",
                            "Applying a template OVERRIDES the view's own graphic settings, and the previous " +
                            "settings are not recoverable from the model afterwards. On a view somebody has " +
                            "adjusted by hand, that is a loss rather than a fix."
                        },
                        ExpectedOutcome = "the named template is applied to the view.",
                        Verification = "horizun_manage_views re-reads the view's template after the commit; the " +
                                       "audit check reports one fewer view without a template."
                    }
                },

                // ACTIONABLE AND DESTRUCTIVE. A group type with zero placed instances
                // carries its full geometry in the file and appears in no view; the
                // audit exists to find them. Deleting one is a delete, and it is offered
                // as one: selected by finding, narrowed by id, rehearsed by
                // horizun_delete_verified, confirmed by token.
                {
                    AuditCheckNames.OrphanGroupTypes, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.OrphanGroupTypes,
                        Tool = "horizun_delete_verified",
                        FixedArguments = new JObject { ["mode"] = "ids" },
                        ElementsArgument = "ids",
                        RequiresExplicitSelection = true,
                        Risk = "high",
                        Reversible = false,
                        DryRunSupported = true,
                        ConfirmationRequired = true,
                        ExpectedOutcome = "the listed group types, none of which is placed, are deleted from the " +
                                          "model. " + DestructiveMeans,
                        Verification = "horizun_delete_verified re-resolves every id after the commit and reports " +
                                       "deleted only for an id that no longer exists; the audit check reports " +
                                       "that many fewer orphan group types."
                    }
                },

                // ACTIONABLE AND DESTRUCTIVE, FILTERED. The rooms finding names two
                // different problems: a room with NO location - it exists in schedules
                // and bounds nothing - and a room that is placed but not enclosed. Only
                // the first is a deletion; the second is a wall somebody has to move.
                // The filter reads the typed problem_code, never the sentence.
                {
                    AuditCheckNames.Rooms, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.Rooms,
                        Tool = "horizun_delete_verified",
                        FixedArguments = new JObject { ["mode"] = "ids" },
                        ElementsArgument = "ids",
                        ItemFilterField = "problem_code",
                        ItemFilterValues = new List<string> { RoomProblemCode.Unplaced },
                        RequiresExplicitSelection = true,
                        Risk = "high",
                        Reversible = false,
                        DryRunSupported = true,
                        ConfirmationRequired = true,
                        ExpectedOutcome = "the listed UNPLACED rooms - rooms with no location - are deleted, so " +
                                          "they stop appearing in schedules with an area of zero. Rooms that are " +
                                          "placed and not enclosed are NOT covered: their boundary is a " +
                                          "modelling decision. " + DestructiveMeans,
                        Verification = "horizun_delete_verified re-resolves every id after the commit; the audit's " +
                                       "rooms check reports that many fewer problems."
                    }
                },

                // UNSUPPORTED, and the reason is the point. Converting an in-place
                // family to a loadable one is a modelling decision with a geometry
                // rebuild inside it; no argument makes it mechanical.
                {
                    AuditCheckNames.InPlaceFamilies, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.InPlaceFamilies,
                        CannotAutomateBecause =
                            "an in-place family becomes a loadable one by being MODELLED again in a family " +
                            "document: its geometry, parameters, host relationships and every instance's " +
                            "placement are decisions, not arguments. A tool that did this automatically would " +
                            "be guessing at somebody's design intent and committing the guess."
                    }
                },

                // UNSUPPORTED. Deleting somebody's CAD import is not a correction
                // this surface gets to propose.
                {
                    AuditCheckNames.ImportedCad, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.ImportedCad,
                        CannotAutomateBecause =
                            "an imported CAD file may be scaffolding somebody is still working from, or it " +
                            "may be the reason a view reads correctly. Deleting it is irreversible in the " +
                            "session and the judgement belongs to whoever imported it. The finding stands; " +
                            "the correction is a conversation."
                    }
                },

                // UNSUPPORTED. A view off a sheet is a list to review, not a defect:
                // working views live off sheets all project long, and placing one
                // means choosing a sheet and a position for it.
                {
                    AuditCheckNames.ViewsOffSheets, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.ViewsOffSheets,
                        CannotAutomateBecause =
                            "a view that is on no sheet is not wrong - working views are meant to live off " +
                            "sheets - and 'fixing' it means either deleting somebody's working view or placing " +
                            "it on a sheet, which is a documentation decision (which sheet, where, at what " +
                            "scale). The audit lists them to review; horizun_pack_sheets is the typed surface " +
                            "for placing the ones somebody chooses."
                    }
                },

                // UNSUPPORTED. No typed command moves an element between worksets, and
                // the right workset is the declared rule's opinion about somebody's
                // model, on a workshared document where the element may be borrowed.
                {
                    AuditCheckNames.WorksetPlacement, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.WorksetPlacement,
                        CannotAutomateBecause =
                            "no typed command in this bridge moves an element to another workset, and one is " +
                            "not improvised here. On a workshared model the element may be owned by somebody " +
                            "else, and moving it changes what their next synchronize brings. The finding names " +
                            "the elements and the workset the rule expected; the move is theirs to make."
                    }
                },

                // UNSUPPORTED. A warning is Revit saying the model contradicts itself,
                // and each contradiction has its own fix.
                {
                    AuditCheckNames.Warnings, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.Warnings,
                        CannotAutomateBecause =
                            "a warning is Revit reporting a specific contradiction - overlapping walls, a room " +
                            "in two places, an unjoined beam - and each one is resolved by a different edit to " +
                            "different elements. There is no single typed operation that 'fixes warnings', and " +
                            "deleting the failing elements is not a fix."
                    }
                },

                // UNSUPPORTED. Closing an open connector is routing.
                {
                    AuditCheckNames.OpenMepConnectors, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.OpenMepConnectors,
                        CannotAutomateBecause =
                            "an open connector is a run that stops mid-air, and closing it means deciding " +
                            "where the run continues to - a route, a fitting, a cap, or the end of a system. " +
                            "horizun_plan_mep is the typed surface for routing; the audit only names the stub."
                    }
                },

                // UNSUPPORTED. Accepting or deleting a design option is design.
                {
                    AuditCheckNames.DesignOptions, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.DesignOptions,
                        CannotAutomateBecause =
                            "a design option holds geometry somebody is still deciding about. Accepting the " +
                            "primary and deleting the rest is the decision itself, and it discards every " +
                            "alternative permanently. The audit reports the options so the decision is made " +
                            "before delivery, not made by the delivery."
                    }
                },

                // UNSUPPORTED. Moving a project's coordinates rewrites where the
                // building is.
                {
                    AuditCheckNames.Coordinates, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.Coordinates,
                        CannotAutomateBecause =
                            "an element far from the internal origin, a reflected link or a rotated one is " +
                            "corrected by moving geometry or re-acquiring shared coordinates, and both change " +
                            "where every element in the model is. That is a coordination decision taken with " +
                            "the other disciplines, not an argument."
                    }
                },

                // UNSUPPORTED. Two levels a millimetre apart are merged by deciding
                // which one every element on the other now belongs to.
                {
                    AuditCheckNames.Datums, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.Datums,
                        CannotAutomateBecause =
                            "a duplicate or coincident level or grid is resolved by choosing which one " +
                            "survives and re-hosting everything on the other, or by renaming one - and a grid " +
                            "off the building's angle may be a rotated wing that is entirely correct. Every " +
                            "one of those is a decision about somebody's building."
                    }
                },

                // UNSUPPORTED. Readiness is evidence the model carries; filling it in
                // is somebody's planning data.
                {
                    AuditCheckNames.Readiness, new CorrectionRecipe
                    {
                        FindingType = AuditCheckNames.Readiness,
                        CannotAutomateBecause =
                            "a role with no value on any element is corrected by SUPPLYING the values - activity " +
                            "ids, cost codes, classifications - and those come from a programme or a cost plan " +
                            "this bridge does not have. horizun_write_params_verified writes them once somebody " +
                            "provides them; nothing here invents them."
                    }
                }
            };

        /// <summary>
        /// The registry. Read-only: an entry added at run time would be an entry
        /// nobody reviewed, which is the whole thing this file exists to prevent.
        /// </summary>
        public static IReadOnlyDictionary<string, CorrectionRecipe> Default
        {
            get { return _default; }
        }

        /// <summary>Every tool this registry can name. Nothing that runs arbitrary code.</summary>
        public static JArray ToolsJson()
        {
            var tools = new List<string>();
            foreach (KeyValuePair<string, CorrectionRecipe> kv in _default)
                if (kv.Value.Tool != null && !tools.Contains(kv.Value.Tool)) tools.Add(kv.Value.Tool);
            tools.Sort(StringComparer.Ordinal);
            var a = new JArray();
            foreach (string t in tools) a.Add(t);
            return a;
        }

        /// <summary>The same list as strings, for the tests that hold other lists to it.</summary>
        public static IEnumerable<string> Tools()
        {
            foreach (JToken t in ToolsJson()) yield return (string)t;
        }

        public static JObject Describe()
        {
            var entries = new JArray();
            foreach (KeyValuePair<string, CorrectionRecipe> kv in _default)
                entries.Add(new JObject
                {
                    ["finding_type"] = kv.Key,
                    ["tool"] = kv.Value.Tool,
                    ["cannot_automate_because"] = kv.Value.CannotAutomateBecause,
                    ["required_inputs"] = new JArray(kv.Value.RequiredArguments.ToArray()),
                    // PUBLISHED, so a client can see before it calls that this entry will
                    // refuse an action that names no ids. A requirement nobody can read
                    // until it fires is a refusal that looks like a bug.
                    ["requires_explicit_selection"] = kv.Value.RequiresExplicitSelection,
                    ["item_filter"] = kv.Value.ItemFilterField == null
                        ? null
                        : new JObject
                        {
                            ["field"] = kv.Value.ItemFilterField,
                            ["values"] = new JArray(kv.Value.ItemFilterValues.ToArray())
                        },
                    // PUBLISHED for the same reason as the filter: a client that
                    // reads `corrected` should be able to see what was checked.
                    ["postcondition"] = kv.Value.Postcondition,
                    ["risk"] = kv.Value.Risk,
                    ["reversible"] = kv.Value.Reversible
                });
            return new JObject
            {
                ["entries"] = entries,
                ["tools"] = ToolsJson(),
                ["postcondition_means"] = CorrectionPostcondition.Means,
                ["registry_means"] = RegistryMeans,
                ["refusal_means"] = RefusalMeans,
                ["destructive_means"] = DestructiveMeans,
                ["execution"] = "horizun_apply_corrections: select findings by finding_id, rehearse, confirm " +
                                "with the token the rehearsal issued, apply, re-audit."
            };
        }
    }

    /// <summary>
    /// The typed codes the rooms finding stamps on each item beside its sentence.
    /// Declared once so the audit and the registry's filter cannot spell them
    /// differently - a filter that matched nothing would make the correction
    /// silently cover nothing.
    /// </summary>
    public static class RoomProblemCode
    {
        public const string Unplaced = "unplaced";
        public const string NotEnclosed = "not_enclosed";
    }
}
