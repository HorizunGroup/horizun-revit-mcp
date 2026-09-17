// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// WHERE A NESTED SYMBOL ACTUALLY IS.
//
// A block instance inside a block DEFINITION carries coordinates in that
// definition's own system, not the drawing's. Measured on a real permit set: the
// washing-machine symbol reports (0.0, 4.8) and the air-handler symbols report
// coordinates ten times outside the drawing's extents, because none of them is
// placed in model space - they are all drawn inside unit blocks, and the unit
// blocks are what gets placed.
//
// Converting those coordinates directly puts every symbol in the building near
// the origin, in a model that otherwise looks finished. So this composes the
// chain: for every placement of the parent block, the nested symbol appears once,
// transformed by that placement.
//
// THAT IS ALSO WHAT MAKES A REPEATED UNIT WORK. One unit block drawn once and
// placed eight times gives eight sets of symbols, each in its own apartment, each
// with its own identity - and the identity is what stops the second conversion
// building them all again.
//
// WHAT IT REFUSES. A block that contains itself, directly or through a chain, is
// a cycle: it is reported and not expanded, because the expansion does not
// terminate and the drawing is wrong. A chain deeper than the declared limit is
// reported with its depth rather than silently truncated.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One symbol, where it really is in the drawing.</summary>
    public sealed class CadPlacedBlock
    {
        public CadIrEntity Source;              // the instance as read, with its LOCAL coordinates
        public CadPoint At;                     // where it is in the drawing
        public double RotationRadians;
        public bool Mirrored;
        /// <summary>The magnitude of the scale this placement accumulated through its parents.</summary>
        public double Scale = 1.0;

        /// <summary>
        /// A point of this block's DEFINITION, where this placement puts it - the
        /// same composition the nested placements below use: scale, the
        /// reflection about the block's own Y axis, then the turn.
        /// </summary>
        public CadPoint Place(CadPoint local)
        {
            double lx = local.X * Scale, ly = local.Y * Scale;
            if (Mirrored) lx = -lx;
            double cos = Math.Cos(RotationRadians), sin = Math.Sin(RotationRadians);
            return new CadPoint(At.X + lx * cos - ly * sin, At.Y + lx * sin + ly * cos, At.Z + local.Z * Scale);
        }

        /// <summary>Outermost first: the blocks this symbol was nested inside, if any.</summary>
        public List<string> Path = new List<string>();

        /// <summary>
        /// The space the ROOT of this placement was drawn in - "model", "paper",
        /// or null when the reading could not tell.
        ///
        /// A symbol nested four blocks deep is in whatever space placed the
        /// outermost one, which is why this travels down from the root rather
        /// than being read off the instance.
        /// </summary>
        public string Space;

        /// <summary>
        /// The handle of the MODEL-SPACE instance this placement came through.
        ///
        /// Two apartments built from one unit block are the same symbol at two
        /// places, and this is what tells them apart in the identity - without it
        /// every copy of a repeated unit would collapse onto one element.
        /// </summary>
        public string ThroughHandle;

        /// <summary>
        /// The handles of every insertion this placement came through, outermost
        /// first, ending with its own. Names are not enough: a block placed twice
        /// inside one parent puts the same child at two places under the same
        /// names, and only the handles tell the two placements apart.
        /// </summary>
        public List<string> HandleChain = new List<string>();

        /// <summary>A key unique to this placement in this reading, stable across readings of the same file.</summary>
        public string Key => string.Join("/", HandleChain.Select(h => string.IsNullOrEmpty(h) ? "?" : h));

        public string BlockName { get { return Source == null ? null : Source.BlockName; } }
        public string Layer { get { return Source == null ? null : Source.Layer; } }

        /// <summary>The instance as it would have been read had it been drawn here.</summary>
        public CadIrEntity AsEntity()
        {
            var e = new CadIrEntity
            {
                Id = Source.Id + (ThroughHandle == null ? "" : "@" + ThroughHandle),
                Handle = Source.Handle,
                Kind = CadEntityKind.BlockInstance,
                Layer = Source.Layer,
                BlockName = Source.BlockName,
                RotationRadians = RotationRadians,
                ScaleX = Mirrored ? -1 : 1,
                ScaleY = 1,
                Attributes = Source.Attributes
            };
            e.Points.Add(At);
            foreach (string p in Path) e.BlockPath.Add(p);
            return e;
        }
    }

    public sealed class CadPlacementReading
    {
        public List<CadPlacedBlock> Placed = new List<CadPlacedBlock>();

        /// <summary>Block names that contain themselves. Reported, never expanded.</summary>
        public List<string> Cycles = new List<string>();

        /// <summary>Instances inside a block that nothing places in model space.</summary>
        public List<string> Unplaced = new List<string>();

        public int MaxDepthSeen;
        public bool DepthLimitHit;

        public JObject SummaryJson() => new JObject
        {
            ["placed"] = Placed.Count,
            ["at_model_level"] = Placed.Count(p => p.Path.Count == 0),
            ["nested"] = Placed.Count(p => p.Path.Count > 0),
            ["max_nesting_depth"] = MaxDepthSeen,
            ["depth_limit_hit"] = DepthLimitHit,
            ["cycles"] = new JArray(Cycles),
            ["block_definitions_nothing_places"] = new JArray(Unplaced.Take(100)),
            ["means"] = "a symbol drawn inside a block carries that block's coordinates, so it is reported here " +
                        "once per PLACEMENT of the block - which is also how one unit drawn once and placed " +
                        "eight times becomes eight apartments. A definition nothing places contributes nothing " +
                        "to the drawing and nothing here."
        };
    }

    public static class CadBlockPlacement
    {
        public const int DefaultMaxDepth = 8;

        /// <summary>
        /// Every block instance, at the place it actually occupies.
        ///
        /// <paramref name="entities"/> is the whole IR reading: instances at model
        /// level are taken as they are, and instances inside a definition are
        /// expanded once per placement of that definition.
        /// </summary>
        public static CadPlacementReading Place(IList<CadIrEntity> entities, int maxDepth = DefaultMaxDepth)
        {
            var reading = new CadPlacementReading();
            if (entities == null) return reading;

            var instances = entities.Where(e => e != null && e.Kind == CadEntityKind.BlockInstance &&
                                                e.Points.Count > 0).ToList();

            // Instances by the block definition they are drawn INSIDE. An empty
            // path means model or paper space - the drawing itself.
            var inside = new Dictionary<string, List<CadIrEntity>>(StringComparer.OrdinalIgnoreCase);
            var atModelLevel = new List<CadIrEntity>();
            foreach (CadIrEntity e in instances)
            {
                if (e.BlockPath.Count == 0) { atModelLevel.Add(e); continue; }
                string owner = e.BlockPath[e.BlockPath.Count - 1];
                List<CadIrEntity> bucket;
                if (!inside.TryGetValue(owner, out bucket)) inside[owner] = bucket = new List<CadIrEntity>();
                bucket.Add(e);
            }

            // Which definitions are actually placed, so a definition nothing uses
            // can be named rather than silently contributing nothing.
            var placedNames = new HashSet<string>(atModelLevel.Select(e => e.BlockName ?? ""),
                                                  StringComparer.OrdinalIgnoreCase);
            foreach (string owner in inside.Keys)
                if (!placedNames.Contains(owner)) reading.Unplaced.Add(owner);

            foreach (CadIrEntity root in atModelLevel)
            {
                double rootRotation = NormalisedRotation(root);
                reading.Placed.Add(new CadPlacedBlock
                {
                    Source = root,
                    At = root.Points[0],
                    RotationRadians = rootRotation,
                    Mirrored = IsMirrored(root),
                    Scale = ScaleOf(root),
                    ThroughHandle = root.Handle,
                    Space = root.Space,
                    HandleChain = new List<string> { root.Handle ?? root.Id }
                });
                Expand(reading, inside, root, root.Points[0], rootRotation, IsMirrored(root),
                       ScaleOf(root), new List<string> { root.BlockName ?? "" }, root.Handle, 1, maxDepth,
                       root.Space, new List<string> { root.Handle ?? root.Id });
            }

            return reading;
        }

        private static bool IsMirrored(CadIrEntity e)
        {
            bool x = e.ScaleX.HasValue && e.ScaleX.Value < 0;
            bool y = e.ScaleY.HasValue && e.ScaleY.Value < 0;
            return x ^ y;   // both negative is a 180 degree turn, not a reflection
        }

        /// <summary>
        /// The rotation of an insertion written as "turn, then at most a reflection
        /// about the block's own Y axis" - the one form everything downstream
        /// assumes. A negative Y scale is that reflection plus half a turn, and two
        /// negative scales are half a turn and no reflection at all; reading the
        /// raw angle for either points the symbol the wrong way round.
        /// </summary>
        public static double NormalisedRotation(CadIrEntity e)
        {
            double r = e.RotationRadians ?? 0;
            if (e.ScaleY.HasValue && e.ScaleY.Value < 0) r += Math.PI;
            r = r % (2 * Math.PI);
            if (r < 0) r += 2 * Math.PI;
            return r;
        }

        private static double ScaleOf(CadIrEntity e)
        {
            double sx = e.ScaleX.HasValue ? Math.Abs(e.ScaleX.Value) : 1.0;
            return sx <= 0 ? 1.0 : sx;
        }

        private static void Expand(CadPlacementReading reading,
                                   Dictionary<string, List<CadIrEntity>> inside,
                                   CadIrEntity parent, CadPoint parentAt, double parentRotation,
                                   bool parentMirrored, double parentScale,
                                   List<string> path, string throughHandle, int depth, int maxDepth,
                                   string space, List<string> handles)
        {
            if (depth > reading.MaxDepthSeen) reading.MaxDepthSeen = depth;
            if (depth > maxDepth) { reading.DepthLimitHit = true; return; }

            List<CadIrEntity> children;
            if (!inside.TryGetValue(parent.BlockName ?? "", out children)) return;

            foreach (CadIrEntity child in children)
            {
                // A BLOCK THAT CONTAINS ITSELF DOES NOT TERMINATE. AutoCAD does not
                // allow it and files produced by conversion sometimes have it
                // anyway, so it is checked rather than assumed away.
                if (path.Contains(child.BlockName ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    if (!reading.Cycles.Contains(child.BlockName ?? ""))
                        reading.Cycles.Add(child.BlockName ?? "");
                    continue;
                }

                CadPoint local = child.Points[0];
                double cos = Math.Cos(parentRotation), sin = Math.Sin(parentRotation);
                double lx = local.X * parentScale, ly = local.Y * parentScale;
                if (parentMirrored) lx = -lx;                  // the parent's reflection, about its own Y axis

                var at = new CadPoint(parentAt.X + lx * cos - ly * sin,
                                      parentAt.Y + lx * sin + ly * cos,
                                      parentAt.Z + local.Z * parentScale);

                double childRotation = NormalisedRotation(child);
                double rotation = childRotation + parentRotation;
                if (parentMirrored) rotation = parentRotation - childRotation;

                // KEPT IN ONE TURN. Composing four levels of nesting produced 675
                // degrees on a real drawing - the same orientation as 315, and a
                // number no reviewer can read and no validator should have to
                // accept. Revit takes it either way; a person does not.
                rotation = rotation % (2 * Math.PI);
                if (rotation < 0) rotation += 2 * Math.PI;
                bool mirrored = parentMirrored ^ IsMirrored(child);

                var next = new List<string>(path) { child.BlockName ?? "" };
                reading.Placed.Add(new CadPlacedBlock
                {
                    Source = child,
                    At = at,
                    RotationRadians = rotation,
                    Mirrored = mirrored,
                    Scale = parentScale * ScaleOf(child),
                    Path = new List<string>(path),
                    ThroughHandle = throughHandle,
                    Space = space,
                    HandleChain = new List<string>(handles) { child.Handle ?? child.Id }
                });

                Expand(reading, inside, child, at, rotation, mirrored,
                       parentScale * ScaleOf(child), next, throughHandle, depth + 1, maxDepth, space,
                       new List<string>(handles) { child.Handle ?? child.Id });
            }
        }

        /// <summary>
        /// Placements that occupy the same place, the same layer and the same block.
        ///
        /// A drawing where the same symbol sits six times on one point is a real
        /// thing - measured on a permit set - and it is a drafting artefact, not
        /// six fixtures. Building all six gives six identical elements no audit can
        /// tell apart and no schedule can explain; building one and NAMING the rest
        /// is the only version of this that can be checked.
        /// </summary>
        public static List<CadPlacedBlock> Distinct(IList<CadPlacedBlock> placed, double toleranceMm,
                                                    out List<JObject> duplicates)
        {
            return Distinct(placed, toleranceMm, out duplicates, null);
        }

        /// <summary>As above, and <paramref name="dropped"/> receives each collapsed placement with the one kept for it.</summary>
        public static List<CadPlacedBlock> Distinct(IList<CadPlacedBlock> placed, double toleranceMm,
                                                    out List<JObject> duplicates,
                                                    IDictionary<CadPlacedBlock, CadPlacedBlock> dropped)
        {
            var kept = new List<CadPlacedBlock>();
            var groups = new Dictionary<string, List<CadPlacedBlock>>(StringComparer.Ordinal);
            duplicates = new List<JObject>();
            if (placed == null) return kept;

            double step = toleranceMm <= 0 ? 1.0 : toleranceMm;
            foreach (CadPlacedBlock p in placed)
            {
                string key = (p.Layer ?? "") + "" + (p.BlockName ?? "") + "" +
                             Math.Round(p.At.X / step).ToString(CultureInfo.InvariantCulture) + "" +
                             Math.Round(p.At.Y / step).ToString(CultureInfo.InvariantCulture);
                List<CadPlacedBlock> bucket;
                if (!groups.TryGetValue(key, out bucket))
                {
                    groups[key] = new List<CadPlacedBlock> { p };
                    kept.Add(p);
                }
                else
                {
                    bucket.Add(p);
                    if (dropped != null) dropped[p] = bucket[0];
                }
            }

            foreach (var g in groups.Where(g => g.Value.Count > 1))
                duplicates.Add(new JObject
                {
                    ["block_name"] = g.Value[0].BlockName,
                    ["layer"] = g.Value[0].Layer,
                    ["at_mm"] = new JArray(Math.Round(g.Value[0].At.X, 2), Math.Round(g.Value[0].At.Y, 2)),
                    ["copies"] = g.Value.Count,
                    ["kept"] = 1,
                    ["means"] = "the same symbol is drawn " + g.Value.Count.ToString(CultureInfo.InvariantCulture) +
                                " times on one point. ONE was converted: building them all gives identical " +
                                "elements at one place that no audit can tell apart and no schedule can " +
                                "explain. If they are meant to be separate fixtures, they are in the wrong place."
                });
            return kept;
        }
    }
}
