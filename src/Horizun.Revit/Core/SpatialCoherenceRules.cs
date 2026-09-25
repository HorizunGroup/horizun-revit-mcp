// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Spatial coherence: the judgement an expert applies when looking at what was just
// modelled. A write that re-reads its own postconditions proves the request was
// carried out; it does not prove the result makes sense. Measured in field use: a
// modelling session committed a column and a door in the same place, every
// postcondition true. Nobody asked the model whether the two solids could coexist.
//
// This file is the pure half - no Revit API - so the rules are unit-tested: given
// two elements' categories and how they relate (host, joined, connected, same type,
// how much solid they share), is the intersection an error, a warning, expected, or
// nothing? SpatialCoherence.cs measures those facts in Revit and asks these rules.
//
// Categories are BuiltInCategory NAMES ("OST_Doors"), which is what the add-in
// reads from Category.BuiltInCategory in every supported year.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    internal static class SpatialCoherenceRules
    {
        /// <summary>Below this shared volume two solids are treated as touching, not overlapping (about 28 cm³).</summary>
        public const double TouchVolumeFt3 = 1e-3;
        /// <summary>A duplicate shares at least this fraction of the smaller solid.</summary>
        public const double DuplicateShare = 0.95;

        public enum Kind { None, Expected, Duplicate, Conflict, Overlap, Clash }

        public sealed class Pair
        {
            public string CategoryA, CategoryB;
            public bool SameType;
            /// <summary>One hosts the other (door in its wall, fixture on its face, rebar in its host).</summary>
            public bool HostRelation;
            /// <summary>JoinGeometryUtils joined, or the walls meet at a location-curve join.</summary>
            public bool Joined;
            /// <summary>Connected through MEP connectors.</summary>
            public bool Connected;
            /// <summary>Nested family / super-component, or both belong to the same curtain system or stair.</summary>
            public bool SameAssembly;
            /// <summary>Shared solid volume in ft³; null when Revit could not compute the boolean.</summary>
            public double? SharedVolume;
            public double? VolumeA, VolumeB;
        }

        public sealed class Verdict
        {
            public Kind Kind;
            public string Severity;   // error | warning | null
            public string Reason;
            public string Suggestion;
        }

        private static readonly HashSet<string> Openings = Set("OST_Doors", "OST_Windows");
        private static readonly HashSet<string> Structure = Set("OST_StructuralColumns", "OST_StructuralFraming",
            "OST_StructuralFoundation", "OST_StructuralTruss", "OST_Columns");
        private static readonly HashSet<string> Enclosure = Set("OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
            "OST_CurtainWallPanels", "OST_CurtainWallMullions", "OST_StructuralFoundation");
        private static readonly HashSet<string> Mep = Set("OST_DuctCurves", "OST_DuctFitting", "OST_DuctAccessory",
            "OST_FlexDuctCurves", "OST_PipeCurves", "OST_PipeFitting", "OST_PipeAccessory", "OST_FlexPipeCurves",
            "OST_CableTray", "OST_CableTrayFitting", "OST_Conduit", "OST_ConduitFitting", "OST_DuctTerminal",
            "OST_Sprinklers", "OST_MechanicalEquipment", "OST_PlumbingEquipment");
        private static readonly HashSet<string> Contents = Set("OST_Furniture", "OST_FurnitureSystems", "OST_Casework",
            "OST_PlumbingFixtures", "OST_SpecialityEquipment", "OST_ElectricalEquipment", "OST_ElectricalFixtures",
            "OST_LightingFixtures", "OST_GenericModel", "OST_Entourage", "OST_Planting", "OST_MedicalEquipment",
            "OST_FoodServiceEquipment");
        /// <summary>Categories whose intersections are how they are built, never findings.</summary>
        private static readonly HashSet<string> Ignored = Set("OST_Rebar", "OST_AreaRein", "OST_PathRein",
            "OST_FabricReinforcement", "OST_Mass", "OST_Topography", "OST_Toposolid", "OST_Site", "OST_Rooms",
            "OST_MEPSpaces", "OST_Areas", "OST_ShaftOpening", "OST_SWallRectOpening", "OST_FloorOpening",
            "OST_RoofOpening", "OST_ColumnOpening", "OST_StructConnections", "OST_StructuralStiffener",
            "OST_Parts", "OST_Assemblies", "OST_RvtLinks", "OST_Lines", "OST_CLines", "OST_SketchLines");
        /// <summary>Pairs that meet by construction: the structural frame, and walls standing on slabs.</summary>
        private static readonly HashSet<string> ExpectedPairs = PairSet(
            "OST_StructuralFraming|OST_StructuralColumns", "OST_StructuralFraming|OST_Floors",
            "OST_StructuralFraming|OST_StructuralFraming", "OST_StructuralFraming|OST_Walls",
            "OST_StructuralFraming|OST_Roofs", "OST_StructuralColumns|OST_Floors",
            "OST_StructuralColumns|OST_StructuralFoundation", "OST_StructuralColumns|OST_Walls",
            "OST_StructuralFoundation|OST_Floors", "OST_StructuralFoundation|OST_Walls",
            "OST_Columns|OST_Walls", "OST_Columns|OST_Floors", "OST_Walls|OST_Floors", "OST_Walls|OST_Roofs",
            "OST_Walls|OST_Ceilings", "OST_Floors|OST_Roofs", "OST_Stairs|OST_Floors", "OST_Stairs|OST_Walls",
            "OST_Stairs|OST_StairsRailing", "OST_StairsRailing|OST_Floors", "OST_Railings|OST_Floors",
            "OST_Railings|OST_Stairs", "OST_StairsRuns|OST_Floors", "OST_StairsLandings|OST_Floors",
            "OST_Ramps|OST_Floors", "OST_CurtainWallPanels|OST_CurtainWallMullions",
            "OST_CurtainWallPanels|OST_Walls", "OST_CurtainWallMullions|OST_Walls", "OST_Ceilings|OST_Floors");

        /// <summary>Is this category part of what the check looks at at all?</summary>
        public static bool Considered(string category) =>
            !string.IsNullOrEmpty(category) && !Ignored.Contains(category);

        public static Verdict Classify(Pair p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (!Considered(p.CategoryA) || !Considered(p.CategoryB)) return None();
            if (p.HostRelation || p.SameAssembly) return new Verdict { Kind = Kind.Expected, Reason = "host or same assembly" };
            if (p.Connected) return new Verdict { Kind = Kind.Expected, Reason = "connected through MEP connectors" };

            // Revit could not compute the shared solid: the solids intersect (the filter said
            // so) but the size is unknown. Keep the finding, never promote it to "clean".
            bool unmeasured = !p.SharedVolume.HasValue;
            double shared = p.SharedVolume ?? 0;
            if (!unmeasured && shared < TouchVolumeFt3) return None();

            string a = p.CategoryA, b = p.CategoryB;
            bool same = a == b;
            if (same && p.SameType && !unmeasured && IsDuplicate(shared, p.VolumeA, p.VolumeB))
                return new Verdict
                {
                    Kind = Kind.Duplicate, Severity = "error",
                    Reason = "two elements of the same type occupy the same space",
                    Suggestion = "delete one of them (horizun_delete_verified), keeping the one that is hosted, tagged or scheduled"
                };

            if (Openings.Contains(a) || Openings.Contains(b))
            {
                string opening = Openings.Contains(a) ? a : b, other = opening == a ? b : a;
                if (Openings.Contains(other) || Structure.Contains(other) || Enclosure.Contains(other) || Mep.Contains(other) ||
                    Contents.Contains(other) || other == "OST_Stairs" || other == "OST_Railings")
                    return new Verdict
                    {
                        Kind = Kind.Conflict, Severity = "error",
                        Reason = Label(opening) + " is blocked by " + Label(other),
                        Suggestion = "move the " + Label(opening) + " along its host or move the " + Label(other) + "; an opening cannot share its space with another solid"
                    };
            }

            if (p.Joined) return new Verdict { Kind = Kind.Expected, Reason = "joined" };
            if (ExpectedPairs.Contains(Key(a, b))) return new Verdict { Kind = Kind.Expected, Reason = "meet by construction" };

            if (Mep.Contains(a) || Mep.Contains(b))
            {
                string mep = Mep.Contains(a) ? a : b, other = mep == a ? b : a;
                if (other == "OST_Walls" || other == "OST_Floors" || other == "OST_Roofs" || other == "OST_Ceilings")
                    return new Verdict { Kind = Kind.Expected, Reason = "penetration of an enclosure (needs a sleeve or opening, not a finding here)" };
                if (Structure.Contains(other))
                    return new Verdict
                    {
                        Kind = Kind.Clash, Severity = "error",
                        Reason = Label(mep) + " runs through " + Label(other),
                        Suggestion = "reroute the " + Label(mep) + " (horizun_resolve_clash) - structure is not cut for MEP without the engineer"
                    };
                return new Verdict
                {
                    Kind = Kind.Clash, Severity = "warning",
                    Reason = Label(a) + " and " + Label(b) + " intersect without being connected",
                    Suggestion = "offset one of them or connect them"
                };
            }

            if (same)
                return new Verdict
                {
                    Kind = Kind.Overlap, Severity = "warning",
                    Reason = "two " + Label(a) + " overlap without being joined",
                    Suggestion = p.SameType ? "one of them is probably a leftover copy" : "trim one, or join them if the overlap is intended"
                };

            if (Contents.Contains(a) || Contents.Contains(b) || Structure.Contains(a) || Structure.Contains(b))
                return new Verdict
                {
                    Kind = Kind.Conflict, Severity = "warning",
                    Reason = Label(a) + " intersects " + Label(b),
                    Suggestion = "move one of them; two physical objects cannot share space"
                };

            return new Verdict
            {
                Kind = Kind.Overlap, Severity = "warning",
                Reason = Label(a) + " intersects " + Label(b),
                Suggestion = "check whether the intersection is intended"
            };
        }

        /// <summary>Clear depth kept free in front of and behind a door, as a share of its width (min 0.6 m).</summary>
        public const double ClearanceMinFt = 0.6 / 0.3048;
        /// <summary>Clear height checked above the door's base, so beams and ceilings overhead are not obstacles.</summary>
        public const double ClearanceHeightFt = 2.0 / 0.3048;

        /// <summary>
        /// An element standing in the swing/passage zone of a door (not touching the door
        /// itself). Walls, columns and stairs make the door unusable; furniture and
        /// fixtures are a warning; slabs, ceilings, openings and the door's own host are
        /// not obstacles.
        /// </summary>
        public static Verdict Clearance(string obstacle, bool isHost)
        {
            if (isHost || !Considered(obstacle) || Openings.Contains(obstacle)) return None();
            if (obstacle == "OST_Floors" || obstacle == "OST_Ceilings" || obstacle == "OST_Roofs" ||
                obstacle == "OST_StructuralFraming" || obstacle == "OST_StructuralFoundation" ||
                obstacle == "OST_Railings" || obstacle == "OST_StairsRailing") return None();
            bool blocks = obstacle == "OST_Walls" || obstacle == "OST_StructuralColumns" || obstacle == "OST_Columns" ||
                          obstacle == "OST_Stairs" || obstacle == "OST_CurtainWallPanels" || obstacle == "OST_CurtainWallMullions";
            return new Verdict
            {
                Kind = Kind.Conflict, Severity = blocks ? "error" : "warning",
                Reason = Label(obstacle) + " stands in the passage in front of a door",
                Suggestion = "keep the door's clear zone free: move the " + Label(obstacle) + " or the door"
            };
        }

        public static bool IsDuplicate(double shared, double? volumeA, double? volumeB)
        {
            if (!volumeA.HasValue || !volumeB.HasValue) return false;
            double small = Math.Min(volumeA.Value, volumeB.Value), large = Math.Max(volumeA.Value, volumeB.Value);
            if (small <= 0 || large <= 0) return false;
            return shared >= DuplicateShare * small && small >= DuplicateShare * large;
        }

        /// <summary>A readable name for a category key: OST_StructuralColumns -> structural column.</summary>
        public static string Label(string category)
        {
            switch (category)
            {
                case "OST_Doors": return "door";
                case "OST_Windows": return "window";
                case "OST_Walls": return "wall";
                case "OST_Floors": return "floor";
                case "OST_Roofs": return "roof";
                case "OST_Ceilings": return "ceiling";
                case "OST_Columns": return "column";
                case "OST_StructuralColumns": return "structural column";
                case "OST_StructuralFraming": return "beam";
                case "OST_StructuralFoundation": return "foundation";
                case "OST_DuctCurves": return "duct";
                case "OST_PipeCurves": return "pipe";
                case "OST_CableTray": return "cable tray";
                case "OST_Conduit": return "conduit";
                case "OST_Stairs": return "stair";
                case "OST_Railings": return "railing";
                case "OST_Furniture": return "furniture";
                case "OST_PlumbingFixtures": return "plumbing fixture";
                case "OST_GenericModel": return "generic model";
            }
            if (string.IsNullOrEmpty(category)) return "element";
            string s = category.StartsWith("OST_", StringComparison.Ordinal) ? category.Substring(4) : category;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
                sb.Append(char.ToLowerInvariant(s[i]));
            }
            return sb.ToString();
        }

        public static string Key(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

        private static Verdict None() => new Verdict { Kind = Kind.None };
        private static HashSet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.Ordinal);
        private static HashSet<string> PairSet(params string[] pairs)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string p in pairs) { string[] ab = p.Split('|'); set.Add(Key(ab[0], ab[1])); }
            return set;
        }
    }
}
