using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Contracts
{
    /// <summary>
    /// WHICH EDITION THIS BUILD IS. One code base, two products: the Pro build carries every tool; the Community build
    /// (<c>-p:HorizunEdition=community</c>, which defines HORIZUN_COMMUNITY) carries the same code with the Pro-side tools
    /// left OUT OF THE CONTRACT. A tool outside the contract is not listed, cannot be called through horizun_call_tool,
    /// is not registered by the add-in (Dispatcher.Register skips it), is left out of group and toolset member lists, and
    /// moves the contract hash, so a Community server never talks to a Pro add-in
    /// (or the reverse) without saying so.
    ///
    /// The frontier, by use: Community keeps queries, quantities, sheets and basic annotation, simple architecture and
    /// structure without reinforcement. Pro keeps complete electrical and plumbing MEP, families, coordination
    /// (links, clashes, federation, copying between models) and DWG, IFC and point-cloud conversion.
    /// </summary>
    public static class Edition
    {
#if HORIZUN_COMMUNITY
        public const string Name = "community";
#else
        public const string Name = "pro";
#endif

        public static bool IsCommunity => Name == "community";

        /// <summary>The tools only the Pro edition carries. Data, so a test can hold it against the contract.</summary>
        public static readonly IReadOnlyList<string> ProOnlyTools = Array.AsReadOnly(new[]
        {
            // MEP: electrical and plumbing systems, routing, fittings, connections and their audit.
            "horizun_electrical", "horizun_mep_routing", "horizun_plan_mep", "horizun_mep_fittings",
            "horizun_connect_mep", "horizun_plumbing_fixtures", "horizun_manage_system_types",
            "horizun_audit_mep_connectivity", "horizun_cad_connect",
            // Reinforcement: plan, build and audit.
            "horizun_plan_reinforcement", "horizun_apply_reinforcement", "horizun_audit_reinforcement",
            // DWG: links, reading, conversion plans, networks, updates, symbols and joinery from CAD.
            "horizun_manage_cad_links", "horizun_query_cad", "horizun_cad_review", "horizun_cad_extract",
            "horizun_plan_from_cad", "horizun_apply_cad_plan", "horizun_cad_networks", "horizun_plan_cad_update",
            "horizun_apply_cad_update", "horizun_cad_symbols", "horizun_cad_unit_instances",
            "horizun_place_joinery_from_cad", "horizun_audit_cad_model",
            // IFC and point clouds.
            "horizun_plan_from_ifc", "horizun_apply_ifc_plan", "horizun_deliver_ifc",
            "horizun_plan_from_scan", "horizun_apply_scan_plan",
            // Families.
            "horizun_create_family", "horizun_family_reload", "horizun_family_apply", "horizun_query_family_internals",
            // Coordination: links, clashes, federation and copying between models.
            "horizun_manage_links", "horizun_clash", "horizun_resolve_clash", "horizun_coordination",
            "horizun_coordination_view", "horizun_federation_check", "horizun_copy_between_documents",
        });

        /// <summary>False only for a Pro-only tool in the Community build: group and toolset member lists use it so their
        /// counts describe this edition.</summary>
        public static bool Ships(string tool) => !IsCommunity || !ProOnlyTools.Contains(tool);

        /// <summary>The contract this edition ships: the whole list for Pro, without <see cref="ProOnlyTools"/> for Community.</summary>
        public static List<CommandContract> Filter(List<CommandContract> all)
        {
            if (!IsCommunity) return all;
            var pro = new HashSet<string>(ProOnlyTools, StringComparer.Ordinal);
            return all.Where(c => !pro.Contains(c.Name)).ToList();
        }
    }
}
