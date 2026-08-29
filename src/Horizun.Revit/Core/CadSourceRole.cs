// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT KIND OF DRAWING IS THIS, AND WHAT CAN IT HONESTLY SAY?
//
// Every requirement set until now was implicitly about a floor plan, and every
// fixture was one, so nothing ever tested what happens when it is not. A section
// linked into a model looks exactly like a plan to the reader: it is lines on
// layers with x and y. Point a wall rule at one and it converts happily, and the
// building it produces is nonsense - because in a section the horizontal axis is
// a distance ALONG the section line and the vertical axis is height, so an
// element placed at those coordinates lands somewhere no drawing ever put it.
//
// The failure has no symptom in the reply. Walls are created, verified, and
// audited clean against the drawing they came from. Somebody finds it by opening
// the model.
//
// So a source says what it IS, and a rule may only read a producer the view can
// actually show. A REFLECTED CEILING PLAN cannot place a floor. A STRUCTURAL PLAN
// is not where the furniture is. A SECTION does not place anything at all: what a
// section is FOR is heights, and reading heights out of one is a capability this
// bridge does not have yet - so it says so, by name, instead of converting the
// drawing as if it were a plan.
//
// THE ROLE IS DECLARED, NEVER GUESSED FROM A FILE NAME. "A-101-SECTION.dwg" is a
// string somebody typed, and a bridge that read meaning out of it would be
// carrying one office's naming convention into everybody else's project.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class CadSourceRole
    {
        public const string FloorPlan = "floor_plan";
        public const string ReflectedCeilingPlan = "reflected_ceiling_plan";
        public const string StructuralPlan = "structural_plan";
        public const string MepPlan = "mep_plan";
        public const string Section = "section";
        public const string Elevation = "elevation";
        public const string Detail = "detail";
        public const string ReferenceOnly = "reference_only";

        /// <summary>Every role a source may declare, so a reader can switch exhaustively.</summary>
        public static readonly string[] All =
        {
            FloorPlan, ReflectedCeilingPlan, StructuralPlan, MepPlan,
            Section, Elevation, Detail, ReferenceOnly
        };

        /// <summary>
        /// What a role is used when none is declared, and why that is stated in
        /// the reply rather than left silent: every set written before roles
        /// existed was about a floor plan, and staying compatible with them must
        /// not become a way of never saying which view a drawing is.
        /// </summary>
        public const string Default = FloorPlan;

        private static readonly string[] Everything =
        {
            "wall", "curtain_wall", "floor", "ceiling", "roof", "room", "room_separator",
            "column", "structural_column", "beam", "brace", "foundation", "grid", "level",
            "door", "window", "opening", "wall_opening", "shaft", "stair", "railing",
            "furniture", "generic_model",
            "pipe", "duct", "conduit", "cable_tray", "pipe_accessory", "duct_accessory",
            "air_terminal", "plumbing_fixture", "mechanical_equipment", "electrical_fixture"
        };

        private static readonly string[] Mep =
        {
            "pipe", "duct", "conduit", "cable_tray", "pipe_accessory", "duct_accessory",
            "air_terminal", "plumbing_fixture", "mechanical_equipment", "electrical_fixture",
            "grid", "generic_model"
        };

        private static readonly string[] Structural =
        {
            "grid", "structural_column", "column", "beam", "brace", "foundation",
            // IT DRAWS THE WALLS AND THE SLABS, so it draws the holes through them
            // too. Allowing `opening` and `shaft` and refusing `wall_opening` was a
            // gap in the table rather than a statement about the view.
            "floor", "wall", "opening", "wall_opening", "shaft", "generic_model"
        };

        private static readonly string[] Ceiling =
        {
            "ceiling", "grid", "air_terminal", "electrical_fixture", "generic_model", "opening"
        };

        /// <summary>
        /// What this role can be read for, or an empty set when it can be read for
        /// nothing.
        ///
        /// The lists are about what a VIEW SHOWS, not about who owns the layer: a
        /// structural plan legitimately carries the walls the frame sits in, and a
        /// mechanical plan legitimately carries the grid it is dimensioned from.
        /// What none of them carries is somebody else's discipline by accident.
        /// </summary>
        public static IReadOnlyList<string> CanProduce(string role)
        {
            switch (role)
            {
                case FloorPlan: return Everything;
                case StructuralPlan: return Structural;
                case MepPlan: return Mep;
                case ReflectedCeilingPlan: return Ceiling;
                default: return new string[0];
            }
        }

        /// <summary>
        /// Why this role cannot produce anything at all, or null when it can
        /// produce something.
        ///
        /// Each of these is a different reason and they are not interchangeable.
        /// A section is refused because its axes are not model coordinates; a
        /// detail is refused because what it draws is not a building; a
        /// reference-only source is refused because somebody said so.
        /// </summary>
        public static string WhyNothing(string role)
        {
            switch (role)
            {
                case Section:
                    return "a SECTION does not carry model coordinates. Its horizontal axis is a distance " +
                           "ALONG the section line and its vertical axis is height, so an element built at " +
                           "those numbers lands somewhere no drawing ever put it - and it is created, " +
                           "verified and audited clean, because the model and the drawing agree with each " +
                           "other and neither agrees with the building. What a section is FOR is HEIGHTS, " +
                           "which this bridge cannot yet read out of one. Declare the heights on the rule " +
                           "(height_mm, sill_height_mm, head_height_mm, base_level/top_level) and convert " +
                           "the PLAN.";
                case Elevation:
                    return "an ELEVATION does not carry model coordinates either: one of its axes is height " +
                           "and the other is a distance across a facade, neither of which is an x or a y. " +
                           "What it is FOR is sill and head heights and the pattern of a facade, which this " +
                           "bridge cannot yet read out of one. Declare them on the rule and convert the PLAN.";
                case Detail:
                    return "a DETAIL draws how something is made, not where anything is. The lines in it are " +
                           "at a scale and an origin of their own, and nothing in a building corresponds to " +
                           "them one for one.";
                case ReferenceOnly:
                    return "this source is declared reference_only, which is a statement that it is here to " +
                           "be looked at and not to be converted. Change the role if that is not what was " +
                           "meant.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether a rule producing <paramref name="produces"/> may read from this
        /// role, and the sentence to refuse it with when it may not.
        /// </summary>
        public static bool Permits(string role, string produces, out string why)
        {
            why = null;
            string nothing = WhyNothing(role);
            if (nothing != null)
            {
                why = "a rule cannot produce '" + produces + "' from a source whose role is '" + role +
                      "': " + nothing;
                return false;
            }

            IReadOnlyList<string> allowed = CanProduce(role);
            if (allowed.Contains(produces, StringComparer.Ordinal)) return true;

            why = "a rule produces '" + produces + "' and the source declares role '" + role +
                  "', which does not show it. That view can be read for: " +
                  string.Join(", ", allowed.OrderBy(x => x, StringComparer.Ordinal)) +
                  ". A drawing converted through the wrong view produces elements that are created, " +
                  "verified and audited clean - the model and the drawing agree with each other, and " +
                  "neither agrees with the building.";
            return false;
        }

        /// <summary>Is this a role at all? Anything else is a typo, and a typo must not read as a plan.</summary>
        public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);
    }
}
