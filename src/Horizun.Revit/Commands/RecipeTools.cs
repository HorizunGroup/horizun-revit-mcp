// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// The recipe-backed tools. Each one is a name, a description, the recipe that
// carries its geometry, and the counts that must agree; RecipeCommand supplies
// everything that decides whether the answer is true.
//
// These are ports of the Horizun AEC pyRevit buttons. The algorithms are the ones
// that have been run against real models; what changed is that a person clicking
// and confirming became typed arguments and a dry run. See Recipe.cs.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Commands
{
    /// <summary>"Partir Losa" — one floor per sketch loop.</summary>
    public sealed class SplitFloorLoopsCommand : RecipeCommand
    {
        public override string Name => "horizun_split_floor_loops";

        public override string Description =>
            "Split each multi-loop floor into one floor per loop. A slab sketched with several closed loops is " +
            "ONE element, so everything downstream that counts area or schedules by slab treats them as one row.";

        protected override string RecipeName => "split_floor_loops";

        protected override string TransactionName => "Horizun: split floors into loops";

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("floors created", "created", "created_present"),
            new VerifiedCount("original floors deleted", "deleted", "deleted_gone")
        };
    }

    // "Partir Muro Multicapa" USED to live here as a RecipeCommand. It is now a typed
    // command in SplitMultilayerWallsCommand.cs, because the two counts a recipe could
    // verify - how many walls exist, and whether the original is gone - are not the
    // question this operation has to answer. See docs/WALL-LAYER-DECOMPOSITION.md.

    /// <summary>"Separar Losas" — one floor/ceiling per material layer.</summary>
    public sealed class SplitMultilayerSlabsCommand : RecipeCommand
    {
        public override string Name => "horizun_split_multilayer_slabs";

        public override string Description =>
            "Split compound floors and ceilings into one element per material layer, keeping the original " +
            "profile (curves included) and re-hosting the families each one carried.";

        protected override string RecipeName => "split_multilayer_slabs";

        protected override string TransactionName => "Horizun: split compound floors and ceilings";

        protected override string[] ScopeFields => new[] { "element_ids", "view_id", "origin_group_param" };

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("layer slabs created", "created", "created_present"),
            new VerifiedCount("original slabs deleted", "deleted", "deleted_gone")
        };
    }

    /// <summary>"Desagrupar y Marcar" — ungroup, stamping each member with its group.</summary>
    public sealed class UngroupAndMarkCommand : RecipeCommand
    {
        public override string Name => "horizun_ungroup_and_mark";

        public override string Description =>
            "Ungroup model groups, stamping every member with the name of the group it came from so " +
            "horizun_regroup_by_param can put it back. Refuses a group whose members cannot carry the stamp.";

        protected override string RecipeName => "ungroup_and_mark";

        protected override string TransactionName => "Horizun: ungroup and mark origin";

        protected override string[] ScopeFields =>
            new[] { "element_ids", "view_id", "origin_group_param", "marker_view_id" };

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("groups ungrouped", "ungrouped", "groups_gone"),
            new VerifiedCount("elements stamped", "marked", "elements_carrying_the_stamp")
        };
    }

    /// <summary>"Reagrupar" — rebuild a group from the stamp ungroup_and_mark left.</summary>
    public sealed class RegroupByParamCommand : RecipeCommand
    {
        public override string Name => "horizun_regroup_by_param";

        public override string Description =>
            "Rebuild model groups from the origin stamp horizun_ungroup_and_mark wrote: collect every loose " +
            "element carrying a value, group it, and clear the stamp. Annotation is excluded and reported.";

        protected override string RecipeName => "regroup_by_param";

        protected override string TransactionName => "Horizun: regroup by origin parameter";

        // This tool's scope is the PARAMETER and its value, not an id list — the whole
        // point is that it finds the scattered members itself.
        protected override string[] ScopeFields =>
            new[] { "origin_group_param", "origin_value", "group_name_prefix" };

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("groups created", "created", "groups_present"),
            new VerifiedCount("elements grouped", "elements_grouped", "members_confirmed")
        };
    }

    /// <summary>"Adquirir Elevaciones Losa" — copy one slab's warp onto others.</summary>
    public sealed class CopySlabElevationsCommand : RecipeCommand
    {
        public override string Name => "horizun_copy_slab_elevations";

        public override string Description =>
            "Copy a warped floor's surface onto other floors: shape points at their boundary vertices, at " +
            "the source's split-line crossings, and at every source vertex inside them. RESETS any shape " +
            "the destinations already carry.";

        protected override string RecipeName => "copy_slab_elevations";

        protected override string TransactionName => "Horizun: copy slab elevations";

        protected override string[] ScopeFields => new[] { "element_ids", "view_id", "source_floor_id" };

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("floors carrying a warped shape", "floors_shaped", "floors_now_warped")
        };
    }

    /// <summary>"Nivelar Topo V2" — embed floors into a toposolid, top face flush.</summary>
    public sealed class EmbedFloorsInToposolidCommand : RecipeCommand
    {
        public override string Name => "horizun_embed_floors_in_toposolid";

        public override string Description =>
            "Embed floors into a toposolid so their top face sits flush with the terrain, merging slabs " +
            "that touch at the same level into one outline and keeping real steps apart.";

        protected override string RecipeName => "embed_floors_in_toposolid";

        protected override string TransactionName => "Horizun: embed floors in toposolid";

        protected override string[] ScopeFields =>
            new[] { "element_ids", "view_id", "toposolid_id", "offset_cm", "spacing_cm" };

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("shape points on the toposolid", "points_expected", "points_present")
        };
    }

    /// <summary>"Grading TopoSolido" — offset, breaklines and a side slope to daylight.</summary>
    public sealed class GradeToposolidCommand : RecipeCommand
    {
        public override string Name => "horizun_grade_toposolid_around_floors";

        public override string Description =>
            "Grade a toposolid around paths: edge and offset rings, breaklines, and a constant side " +
            "slope run out to where it daylights on the existing terrain. Stations that never daylight " +
            "are reported, not faked.";

        protected override string RecipeName => "grade_toposolid_around_floors";

        protected override string TransactionName => "Horizun: grade toposolid around floors";

        protected override string[] ScopeFields => new[]
        {
            "element_ids", "view_id", "toposolid_id",
            "offset_cm", "edge_spacing_cm", "slope", "max_search_cm", "slope_spacing_cm"
        };

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("shape points on the toposolid", "points_expected", "points_present")
        };
    }

    /// <summary>"Rectangularizar Muros" — irregular orthogonal walls into rectangles.</summary>
    public sealed class RectangularizeWallsCommand : RecipeCommand
    {
        public override string Name => "horizun_rectangularize_walls";

        public override string Description =>
            "Rebuild irregular orthogonal walls as simple rectangular fragments from their real solid " +
            "geometry, re-hosting doors and windows. Refuses curves and non-rectangular openings by name.";

        protected override string RecipeName => "rectangularize_walls";

        protected override string TransactionName => "Horizun: rectangularize walls";

        protected override VerifiedCount[] Verifications => new[]
        {
            new VerifiedCount("wall fragments created", "fragments_created", "fragments_present")
        };
    }
}
