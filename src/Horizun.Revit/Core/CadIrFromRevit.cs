// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// THE FIRST READER: Revit's own imported-geometry walk, adapted to the IR.
//
// This is one adapter, not the reading model. It fills <see cref="CadIr"/> with
// what a DWG that has been imported or linked into Revit will actually give
// back, and — the part that matters — it DECLARES, axis by axis, what it could
// not give and why. Nothing downstream asks "can Revit see text?"; it asks the
// IR whether the text axis is usable, and this file is what makes that question
// answerable instead of guessable.
//
// WHAT THIS READER IS GOOD AT. Geometry and layer names. Revit resolves the
// drawing's units at import time and hands geometry back in its own decimal
// feet, so the millimetres here are correct whenever the import scale was — and
// they are correct without anybody parsing the DWG header.
//
// WHAT IT CANNOT DO, and the difference between the two reasons:
//
//   TEXT       measured on Revit 2026: no string is reachable from imported DWG
//              geometry at any depth. Text arrives as curves on its own layer.
//              The layer name survives; the words do not.
//
//   HANDLES    the walk yields GeometryObject, which has no route back to the
//              DWG entity that produced it. Two revisions of one drawing can
//              therefore only be compared by GEOMETRY, never by identity.
//
//   BLOCKS     a nested GeometryInstance proves a block was placed and gives
//              its nesting depth. It does not give the definition's NAME, so a
//              symbol cannot be recognised by name - only re-derived from the
//              arcs it is drawn with.
//
//   XREFS      the drawing's own external references are resolved by Revit at
//              import and flattened into one instance. What files the drawing
//              referenced is not recoverable from the geometry.
//
// Every one of those is a fact about THIS READER. None of them is a fact about
// DWG files, and none should ever be copied into a sentence that begins "DWG
// does not support".
//
// LAYERS ARE READ TWICE, ON PURPOSE. Once from the geometry, which gives the
// layers something was actually harvested from, and once from the import's own
// subcategories, which gives every layer the drawing DECLARES. A layer in the
// second list and not the first is the single most useful thing this adapter
// produces: it is a layer whose content this reading lost.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadIrFromRevit
    {
        /// <summary>The reader id stamped on every IR this adapter produces.</summary>
        public const string ReaderId = "revit-imported-geometry";

        private const string ReaderWhatItIs =
            "Autodesk Revit's own geometry walk over an imported or linked DWG. It reads what Revit " +
            "materialised at import time, not the DWG file, so everything the import discarded is " +
            "discarded here too.";

        /// <summary>
        /// Turn one harvest into an IR, declaring what the reading could not reach.
        ///
        /// <paramref name="instance"/> is used for the SECOND layer reading - the
        /// import's declared subcategories - and may be null, in which case the
        /// layers axis is downgraded to Partial and says why.
        /// </summary>
        /// <summary>
        /// WHAT A LOOSE PIECE WAS DRAWN AS. The harvest gives a polyline's pieces no
        /// curve id, so they arrive here one by one; they used to become LINE
        /// entities, and a run's semantic id carries its source kind - so every run
        /// drawn as a polyline piece got one id from horizun_cad_networks (read
        /// through this IR) and another from the conversion (read from the harvest).
        /// horizun_cad_connect then found no element for 14 of 20 runs of a real
        /// corridor supply plan while run_identity said the two readings matched.
        /// </summary>
        public static string LooseEntityKind(CadCurveKind sourceKind) =>
            sourceKind == CadCurveKind.Polyline ? CadEntityKind.Polyline : CadEntityKind.Line;

        public static CadIr Adapt(Document doc, Element instance, CadHarvest harvest,
                                  string sourceName, string sourceSha256, string revitVersion)
        {
            if (harvest == null) throw new ArgumentNullException("harvest");

            var ir = new CadIr
            {
                SourceName = sourceName,
                SourceSha256 = sourceSha256,
                DeclaredUnits = null,
                UnitScaleToMm = 1.0,
                UnitScaleSource =
                    "Revit applied the drawing's scale at IMPORT time and returns decimal feet; the harvest " +
                    "converted those to millimetres. The file's own declared unit is not read back, so a " +
                    "wrong import scale is invisible here and would make every length below wrong by a " +
                    "constant factor."
            };

            // ---- entities, rebuilt from the segments and the arcs -------------
            //
            // The harvest chords everything for topology. An arc that survived as
            // an arc is emitted ONCE, as an arc, and its chords are not emitted
            // again - otherwise the IR would carry the same drawn thing twice and
            // every count over it would be inflated.
            var arcsByCurve = new Dictionary<string, CadArcFact>(StringComparer.Ordinal);
            foreach (CadArcFact a in harvest.Arcs)
                if (a != null && a.CurveId != null) arcsByCurve[a.CurveId] = a;

            var byCurve = new Dictionary<string, List<CadSegment>>(StringComparer.Ordinal);
            var loose = new List<CadSegment>();

            foreach (CadSegment s in harvest.Segments)
            {
                if (s.SourceCurveId == null) { loose.Add(s); continue; }
                List<CadSegment> bucket;
                if (!byCurve.TryGetValue(s.SourceCurveId, out bucket))
                    byCurve[s.SourceCurveId] = bucket = new List<CadSegment>();
                bucket.Add(s);
            }

            int n = 0;
            foreach (var kv in byCurve.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                CadArcFact arc;
                List<CadSegment> chords = kv.Value.OrderBy(c => c.SourceIndex).ToList();
                var e = new CadIrEntity
                {
                    Id = "e" + (++n).ToString(CultureInfo.InvariantCulture),
                    Layer = chords[0].Layer
                };

                if (arcsByCurve.TryGetValue(kv.Key, out arc))
                {
                    // AN ARC, AS AN ARC **AND** AS ITS CHORDS.
                    //
                    // The first version carried only start, middle and end - the
                    // three points that define the curve - and CadIr.ToSegments,
                    // which every topology rule consumes, turned them into TWO
                    // chords. A quarter-circle chorded to the declared 5 mm became
                    // one chorded to whatever two straight lines give: on a 300 mm
                    // radius that is roughly 44 mm off the true curve, nine times
                    // the tolerance the caller asked for, and every node, junction
                    // and length downstream was computed from a shape nobody drew.
                    //
                    // So the entity carries BOTH, exactly as the harvest does. The
                    // chords are this entity's own geometry rather than additional
                    // entities, so nothing is counted twice; ToSegments reproduces
                    // what the harvest produced, and ToArcs still hands a rule the
                    // real curve.
                    e.Kind = CadEntityKind.Arc;
                    e.Arc = arc;
                    e.Approximated = true;
                    e.Points.Add(chords[0].A);
                    foreach (CadSegment c in chords) e.Points.Add(c.B);
                }
                else
                {
                    e.Kind = chords.Count > 1 ? CadEntityKind.Polyline : CadEntityKind.Line;
                    e.Approximated = chords[0].SourceKind == CadCurveKind.Spline
                                  || chords[0].SourceKind == CadCurveKind.Arc;
                    e.Points.Add(chords[0].A);
                    foreach (CadSegment c in chords) e.Points.Add(c.B);
                }
                ir.Entities.Add(e);
            }

            foreach (CadSegment s in loose)
                ir.Entities.Add(new CadIrEntity
                {
                    Id = "e" + (++n).ToString(CultureInfo.InvariantCulture),
                    Kind = LooseEntityKind(s.SourceKind),
                    Layer = s.Layer,
                    Points = { s.A, s.B }
                });

            // ---- layers, from two independent sources -------------------------
            var declared = DeclaredLayers(doc, instance);

            // ENTITIES PER LAYER, COUNTED FROM THE IR ITSELF.
            //
            // This used to be filled from harvest.LayerCounts, which counts every
            // GeometryObject the walk VISITED - curves, solids, nested instances -
            // while the IR's entity_count counts what it PRODUCED. The per-layer
            // numbers therefore did not add up to the total, on exactly the layers
            // where it matters: one carrying hatch residue reported more entities
            // than the IR holds, and nothing said they were different units.
            var entitiesPerLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CadIrEntity e in ir.Entities)
            {
                string key = e.Layer ?? "";
                int seen;
                entitiesPerLayer[key] = entitiesPerLayer.TryGetValue(key, out seen) ? seen + 1 : 1;
            }

            var primitivesPerLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in harvest.LayerCounts) primitivesPerLayer[kv.Key] = kv.Value;

            var names = new HashSet<string>(primitivesPerLayer.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string d in declared.Keys) names.Add(d);
            foreach (string e in entitiesPerLayer.Keys) names.Add(e);

            var lostLayers = new List<string>();
            foreach (string name in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                int entities, primitives;
                entitiesPerLayer.TryGetValue(name, out entities);
                bool hasPrimitives = primitivesPerLayer.TryGetValue(name, out primitives);
                CadIrLayer declaredLayer;
                declared.TryGetValue(name, out declaredLayer);

                ir.Layers.Add(new CadIrLayer
                {
                    Name = name,
                    EntityCount = entities,
                    PrimitiveCount = hasPrimitives ? primitives : -1,
                    ColorRgb = declaredLayer == null ? null : declaredLayer.ColorRgb,
                    Off = declaredLayer == null ? (bool?)null : declaredLayer.Off
                });

                // A LAYER THE DRAWING DECLARES AND THIS READING PRODUCED NOTHING
                // FROM. Either the layer is empty, or its content did not survive
                // the walk - and those are not the same news. Measured on ENTITIES
                // now, so a layer holding only solids is named too: the walk saw
                // them and the IR has none of them.
                if (entities == 0 && declaredLayer != null) lostLayers.Add(name);
            }

            if (lostLayers.Count > 0)
                ir.NotRead.Add(new JObject
                {
                    ["what"] = "layers declared by the import with no harvested geometry",
                    ["count"] = lostLayers.Count,
                    ["layers"] = new JArray(lostLayers.Take(200).Select(s => (JToken)s)),
                    ["means"] = "the import declares these layers and this reading produced NO ENTITY from " +
                                "them. Either they are empty in the drawing, or they hold only things this " +
                                "reader drops - text, hatches, dimensions, block attributes - and the two " +
                                "cannot be told apart from inside Revit. Where a layer here has a non-zero " +
                                "primitive_count, the walk DID see something on it and turned none of it " +
                                "into geometry, which narrows it to the second case."
                });

            foreach (JObject o in harvest.NotHarvested) ir.NotRead.Add(o);

            // ---- the capability declaration -----------------------------------
            ir.Reader = Capability(instance, harvest, revitVersion, declared.Count, ir.Entities.Count);
            return ir;
        }

        /// <summary>
        /// WHAT THIS READER CAN AND CANNOT SUPPLY, as a declaration carried with
        /// the reading for the rest of its life.
        ///
        /// Each verdict states its own evidence, and where a verdict was measured
        /// rather than reasoned, it says so and names the version it was measured
        /// on. A verdict with no evidence is an assertion and the axis constructor
        /// refuses to build one.
        /// </summary>
        public static CadReaderCapability Capability(Element instance, CadHarvest harvest,
                                                     string revitVersion, int declaredLayerCount,
                                                     int entityCount)
        {
            var c = new CadReaderCapability(ReaderId, revitVersion ?? "unknown", ReaderWhatItIs);

            bool truncated = harvest != null && harvest.Truncated;
            bool unreadable = harvest != null && harvest.GeometryUnreadable;

            c.Declare(CadAxes.Geometry,
                unreadable ? CadAxisState.Unavailable
                           : truncated ? CadAxisState.Partial : CadAxisState.Supplied,
                unreadable
                    ? "Revit returned no geometry container for this instance at all. That is not an empty " +
                      "drawing: a CAD placed in a single view returns nothing unless that view is passed, and " +
                      "an unloaded link returns nothing either."
                    : truncated
                        ? "the walk stopped at its primitive bound of " +
                          (harvest == null ? 0 : harvest.PrimitiveBound).ToString(CultureInfo.InvariantCulture) +
                          ", so everything past it is absent from every count"
                        : "the whole instance was walked; curves that would not evaluate are listed in not_read",
                entityCount);

            c.Declare(CadAxes.Layers,
                instance == null ? CadAxisState.Partial : CadAxisState.Supplied,
                instance == null
                    ? "layer names were taken only from the geometry's graphics style. Without the import " +
                      "instance, layers the drawing declares but this walk found nothing on cannot be listed, " +
                      "so the layer list is a LOWER BOUND."
                    : "layer names come from the graphics-style category on each leaf primitive, and the full " +
                      "declared list from the import's own subcategories. A name that Revit will not give is " +
                      "recorded as null rather than as a layer called \"\".",
                declaredLayerCount);

            c.Declare(CadAxes.EntityHandles, CadAxisState.Unavailable,
                "the walk yields GeometryObject, which carries no route back to the DWG entity that produced " +
                "it. The file's handles exist; this reading cannot see them. Consequence: two revisions of one " +
                "drawing are comparable only by GEOMETRY, never by identity, so a moved entity and a " +
                "deleted-plus-added pair look the same.");

            c.Declare(CadAxes.Text, CadAxisState.Unavailable,
                "MEASURED on Revit 2026 against a real linked DWG: not one reachable string, on any object, at " +
                "any depth. Text arrives as curves on its own layer - the layer name survives, the words do " +
                "not. Room names, pipe sizes, circuit numbers, equipment tags and elevation callouts are all " +
                "on this axis.");

            c.Declare(CadAxes.BlockNames, CadAxisState.Unavailable,
                "nested GeometryInstance objects prove a block was placed and give its nesting depth - " +
                (harvest == null ? 0 : harvest.InstancePaths.Count).ToString(CultureInfo.InvariantCulture) +
                " were traversed in this reading - but this walk resolves no definition NAME for them. A " +
                "symbol therefore cannot be recognised by name, only re-derived from the arcs it is drawn with.");

            c.Declare(CadAxes.BlockAttributes, CadAxisState.Unavailable,
                "block attributes are text, and text is not reachable. A panel schedule drawn as attributed " +
                "blocks reads here as line work.");

            c.Declare(CadAxes.ExternalReferences, CadAxisState.Unavailable,
                "Revit resolves a drawing's own xrefs at import and flattens them into the instance. Which " +
                "files the drawing referenced, and with what insertion and scale, is not recoverable from the " +
                "geometry. Revit's OWN links between a project and several DWGs are a different thing and are " +
                "readable through horizun_manage_cad_links.");

            c.Declare(CadAxes.Units, CadAxisState.Unavailable,
                "Revit applied the drawing's unit at import and returns decimal feet. The unit the file " +
                "DECLARES is not read back, so a drawing imported at the wrong scale produces geometry that " +
                "is self-consistent, plausible, and wrong by a constant factor - with nothing here able to " +
                "notice.");

            c.Declare(CadAxes.Layouts, CadAxisState.Unavailable,
                "an import brings in one space. Paper-space layouts, their viewports and their scales do not " +
                "arrive, so a sheet set cannot be reconstructed from this reading.");

            c.Declare(CadAxes.Elevation, CadAxisState.Supplied,
                "every point carries its Z, converted from Revit's feet. A plan drawn flat reads as Z=0 " +
                "throughout, and that IS the drawing - not a loss. Whether a flat plan's real elevations were " +
                "written as text is a question for the text axis, which this reader cannot answer.");

            c.Declare(CadAxes.Appearance,
                instance == null ? CadAxisState.Unavailable : CadAxisState.Partial,
                instance == null
                    ? "without the import instance there is no subcategory to read a colour from"
                    : "the RESOLVED line colour of each layer is reachable through the import's subcategories, " +
                      "as r,g,b - not as an AutoCAD colour index, which is a different fact. A per-entity " +
                      "colour override, a lineweight and a linetype are not reachable at all, so two systems " +
                      "distinguished only by entity colour on one shared layer cannot be told apart here.");

            c.Declare(CadAxes.ExtendedData, CadAxisState.Unavailable,
                "XDATA and extension dictionaries do not survive the import. A discipline tool that recorded " +
                "what it knew there - a circuit number, a flow rate, a system id - recorded it somewhere this " +
                "reading cannot look.");

            return c;
        }

        /// <summary>
        /// Every layer the IMPORT declares, with its resolved colour.
        ///
        /// This is the second, independent reading of the layer list, and the
        /// only route by which a layer with no surviving geometry can be named at
        /// all. Returns an empty map rather than throwing when the instance has
        /// no category - a reading that lost the declared list is still a reading,
        /// and the capability says so.
        /// </summary>
        public static Dictionary<string, CadIrLayer> DeclaredLayers(Document doc, Element instance)
        {
            var map = new Dictionary<string, CadIrLayer>(StringComparer.OrdinalIgnoreCase);
            if (instance == null) return map;
            try
            {
                Category cat = instance.Category;
                if (cat == null) return map;
                CategoryNameMap subs = cat.SubCategories;
                if (subs == null) return map;
                foreach (Category sub in subs)
                {
                    if (sub == null || string.IsNullOrEmpty(sub.Name)) continue;
                    var layer = new CadIrLayer { Name = sub.Name, EntityCount = 0 };
                    try
                    {
                        Color col = sub.LineColor;
                        if (col != null && col.IsValid)
                            layer.ColorRgb = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}",
                                                           col.Red, col.Green, col.Blue);
                    }
                    catch { }
                    map[sub.Name] = layer;
                }
            }
            catch { }
            return map;
        }
    }
}
