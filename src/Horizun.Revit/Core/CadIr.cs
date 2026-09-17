// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// THE INTERMEDIATE REPRESENTATION: what a DWG said, apart from who read it.
//
// Until this file existed, "what the drawing contains" and "what Revit's
// imported-geometry walk can reach" were the same object. That is a defensible
// engineering choice right up to the moment somebody asks whether a better
// reader would help — and then it is unanswerable, because the loss is not
// recorded anywhere. A layer with no text looks exactly like a layer whose text
// the reader could not reach, and every rule downstream matches nothing and
// reports a clean zero.
//
// So the reading is split in two:
//
//   THE IR        what was found: layers, entities, blocks, external
//                 references, units, extents. Reader-agnostic, versioned,
//                 serialisable, and comparable between two issues of a file.
//
//   THE READER    what the thing that produced it COULD have found. Declared
//   CAPABILITY    per axis, measured or stated, and carried WITH the IR
//                 forever after.
//
// The second half is the point. A rule that needs entity text asks the IR, and
// gets one of three answers: here it is; this drawing has none; THIS READER
// CANNOT SUPPLY TEXT AND THAT IS WHY YOU SEE NONE. The third answer is the one
// that was previously impossible to give, and it is the difference between "the
// drawing has no room names" and "ask for a reader that can see them".
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO. It does not read a DWG. It does not
// name a reader, a library, or a vendor. It is the shape both sides agree on:
// an adapter over Revit's geometry walk fills it in with most axes declared
// unavailable, and any future adapter — one that parses the file itself — fills
// in more, without one line of the interpretation rules changing. The rules
// consume the IR and the capability, never a reader.
//
// Revit-free, on purpose: this is the contract, and a contract that needs a
// running Revit to inspect is not one anybody can argue with at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// WHETHER AN AXIS OF THE DRAWING REACHED THE IR, AND WHY NOT.
    ///
    /// Four answers, and the difference between the last two is the whole reason
    /// this enum exists. <c>Absent</c> is a statement about the DRAWING.
    /// <c>Unavailable</c> is a statement about the READER. A caller that cannot
    /// tell them apart will read a reader's blind spot as a fact about the
    /// building.
    /// </summary>
    public enum CadAxisState
    {
        /// <summary>The reader supplied this, and what is in the IR is what the file says.</summary>
        Supplied,

        /// <summary>The reader can supply this and the drawing has none. A fact about the DRAWING.</summary>
        Absent,

        /// <summary>The reader supplied part of it, and the part it dropped is named. Counts are LOWER BOUNDS.</summary>
        Partial,

        /// <summary>This reader cannot supply this at all. A fact about the READER, never about the drawing.</summary>
        Unavailable
    }

    /// <summary>One axis of a reading, its verdict, and the evidence for the verdict.</summary>
    public sealed class CadAxis
    {
        public string Name { get; }
        public CadAxisState State { get; }

        /// <summary>
        /// HOW THE VERDICT WAS ARRIVED AT — in words, for the person who has to
        /// decide whether to go and find a different reader. "Measured on Revit
        /// 2026: no string is reachable at any depth" is a usable sentence;
        /// "unavailable" alone is not.
        /// </summary>
        public string Evidence { get; }

        /// <summary>How many of this axis reached the IR. -1 when counting it is meaningless.</summary>
        public int Count { get; }

        public CadAxis(string name, CadAxisState state, string evidence, int count = -1)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("an axis needs a name", "name");
            if (string.IsNullOrWhiteSpace(evidence))
                throw new ArgumentException("an axis verdict without evidence is an assertion", "evidence");
            Name = name; State = state; Evidence = evidence; Count = count;
        }

        /// <summary>
        /// Can a rule that NEEDS this axis be run at all?
        ///
        /// ABSENT COUNTS. The whole point of separating Absent from Unavailable is
        /// that they are different kinds of nothing: Absent means the reader read
        /// this axis and the drawing carries none of it, which is a FINDING and a
        /// rule may act on it - a legend with no text really is a legend with no
        /// text. Unavailable means the reader cannot see the axis at all, so the
        /// same empty result means nothing and a rule must not run.
        ///
        /// Excluding Absent here made a drawing this reader CAN read refuse every
        /// rule that needs the axis, with a message saying nothing is known about
        /// it - which was false, and it is the two-zeros mistake in the one place
        /// built to prevent it.
        /// </summary>
        public bool Usable => State == CadAxisState.Supplied ||
                              State == CadAxisState.Partial ||
                              State == CadAxisState.Absent;

        public static string Word(CadAxisState s)
        {
            switch (s)
            {
                case CadAxisState.Supplied: return "supplied";
                case CadAxisState.Absent: return "absent_from_the_drawing";
                case CadAxisState.Partial: return "partial";
                default: return "unavailable_from_this_reader";
            }
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["axis"] = Name,
                ["state"] = Word(State),
                ["evidence"] = Evidence
            };
            if (Count >= 0) o["count"] = Count;
            // The sentence a caller should repeat to a human, so that the
            // distinction does not have to be re-derived at every call site.
            o["means"] = State == CadAxisState.Unavailable
                ? "NOTHING IS KNOWN ABOUT THE DRAWING ON THIS AXIS. An empty result here is this reader's " +
                  "blind spot, not a finding. Do not report it as 'the drawing has none'."
                : State == CadAxisState.Absent
                    ? "the reader can see this axis and the drawing has none of it - this IS a finding"
                    : State == CadAxisState.Partial
                        ? "some of this axis reached the IR and some did not; every count on it is a LOWER BOUND"
                        : "what is in the IR on this axis is what the file says";
            return o;
        }

        public static CadAxis FromJson(JObject o)
        {
            if (o == null) return null;
            string word = o.Value<string>("state") ?? "unavailable_from_this_reader";
            CadAxisState st = word == "supplied" ? CadAxisState.Supplied
                : word == "absent_from_the_drawing" ? CadAxisState.Absent
                : word == "partial" ? CadAxisState.Partial
                : CadAxisState.Unavailable;
            return new CadAxis(o.Value<string>("axis"), st,
                               o.Value<string>("evidence") ?? "no evidence recorded",
                               o.Value<int?>("count") ?? -1);
        }
    }

    /// <summary>
    /// THE NAMED AXES. A closed vocabulary, so a rule can ask for one by name and
    /// a reader that forgets to declare one is caught rather than assumed capable.
    /// </summary>
    public static class CadAxes
    {
        /// <summary>Lines, arcs, polylines, splines: the drawn geometry itself.</summary>
        public const string Geometry = "geometry";
        /// <summary>The layer each entity sits on, by name.</summary>
        public const string Layers = "layers";
        /// <summary>The DWG handle of each entity - the only stable identity the file itself carries.</summary>
        public const string EntityHandles = "entity_handles";
        /// <summary>TEXT, MTEXT and attribute values: room names, pipe sizes, circuit numbers, elevations.</summary>
        public const string Text = "text";
        /// <summary>Block DEFINITIONS by name, so a symbol can be recognised rather than re-derived from arcs.</summary>
        public const string BlockNames = "block_names";
        /// <summary>Block ATTRIBUTES: the tag/value pairs that carry a panel name or a fixture mark.</summary>
        public const string BlockAttributes = "block_attributes";
        /// <summary>External references: which files this drawing pulls in, and where they sit.</summary>
        public const string ExternalReferences = "external_references";
        /// <summary>The drawing's declared units, as opposed to units a caller asserts.</summary>
        public const string Units = "units";
        /// <summary>Paper-space layouts and their viewports: what a sheet shows and at what scale.</summary>
        public const string Layouts = "layouts";
        /// <summary>Per-entity elevation. A plan drawn flat at Z=0 is not the same as one with real Z.</summary>
        public const string Elevation = "elevation";
        /// <summary>Colour, linetype and lineweight - sometimes the only thing distinguishing two systems on one layer.</summary>
        public const string Appearance = "appearance";
        /// <summary>Extended entity data / XDATA: where a discipline tool hides what it knows.</summary>
        public const string ExtendedData = "extended_data";

        public static readonly string[] All =
        {
            Geometry, Layers, EntityHandles, Text, BlockNames, BlockAttributes,
            ExternalReferences, Units, Layouts, Elevation, Appearance, ExtendedData
        };
    }

    /// <summary>
    /// WHO READ THE FILE AND WHAT THEY COULD SEE.
    ///
    /// Carried with every IR and stamped into everything produced from it, so a
    /// model can be asked not only which drawing built it but WHICH READING of
    /// that drawing - and a conversion that was poor because the reader was thin
    /// can be told from one that was poor because the drawing was.
    /// </summary>
    public sealed class CadReaderCapability
    {
        /// <summary>A stable id for the reading engine, e.g. "revit-imported-geometry".</summary>
        public string ReaderId;
        /// <summary>The engine's own version string, so two readings by different builds are comparable.</summary>
        public string ReaderVersion;
        /// <summary>One sentence: what this reader IS, for someone who has never heard of it.</summary>
        public string ReaderDescription;

        private readonly Dictionary<string, CadAxis> _axes =
            new Dictionary<string, CadAxis>(StringComparer.Ordinal);

        public CadReaderCapability(string readerId, string readerVersion, string description)
        {
            if (string.IsNullOrWhiteSpace(readerId))
                throw new ArgumentException("a reading must name its reader", "readerId");
            ReaderId = readerId;
            ReaderVersion = readerVersion ?? "unknown";
            ReaderDescription = description ?? "";
        }

        public CadReaderCapability Declare(string axis, CadAxisState state, string evidence, int count = -1)
        {
            _axes[axis] = new CadAxis(axis, state, evidence, count);
            return this;
        }

        /// <summary>The verdict on one axis, or null when the reader never declared it.</summary>
        public CadAxis Axis(string name)
        {
            CadAxis a;
            return _axes.TryGetValue(name ?? "", out a) ? a : null;
        }

        /// <summary>
        /// Can a rule that NEEDS this axis run?
        ///
        /// AN UNDECLARED AXIS IS NOT CAPABLE. A reader that forgot to say what it
        /// can do gets the pessimistic answer, because the alternative is a rule
        /// silently matching nothing and a report that says the drawing was empty.
        /// </summary>
        public bool Can(string axis)
        {
            CadAxis a = Axis(axis);
            return a != null && a.Usable;
        }

        /// <summary>The axes this reader declared it cannot supply at all.</summary>
        public IEnumerable<CadAxis> Blind =>
            _axes.Values.Where(a => a.State == CadAxisState.Unavailable)
                        .OrderBy(a => a.Name, StringComparer.Ordinal);

        /// <summary>Axes in the closed vocabulary that this reader never mentioned. Treated as incapable.</summary>
        public IEnumerable<string> Undeclared =>
            CadAxes.All.Where(n => !_axes.ContainsKey(n));

        /// <summary>
        /// The refusal to hand back when a rule needs an axis this reader lacks.
        /// One shape, so every command refuses the same way and a client can
        /// match on it.
        /// </summary>
        public JObject RefusalFor(string axis, string whatNeededIt)
        {
            CadAxis a = Axis(axis);
            return new JObject
            {
                ["refused"] = "reader_cannot_supply_axis",
                ["axis"] = axis,
                ["needed_by"] = whatNeededIt,
                ["reader"] = ReaderId,
                ["reader_version"] = ReaderVersion,
                ["state"] = a == null ? "undeclared" : CadAxis.Word(a.State),
                ["evidence"] = a == null
                    ? "this reader never declared whether it can supply this axis, and an undeclared axis is " +
                      "treated as one it cannot - the pessimistic reading is the only safe one"
                    : a.Evidence,
                ["means"] = "the rule was NOT run and produced no result. This is not a finding about the " +
                            "drawing: nothing was looked at. A reader that supplies this axis would change the answer."
            };
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["reader_id"] = ReaderId,
                ["reader_version"] = ReaderVersion,
                ["reader_description"] = ReaderDescription,
                ["axes"] = new JArray(_axes.Values
                    .OrderBy(a => a.Name, StringComparer.Ordinal)
                    .Select(a => (JToken)a.ToJson())),
                ["undeclared_axes"] = new JArray(Undeclared.Select(n => (JToken)n))
            };
            var blind = Blind.Select(a => a.Name).ToList();
            o["blind_to"] = new JArray(blind.Select(n => (JToken)n));
            o["blind_means"] = blind.Count == 0
                ? "this reader declared no axis it is wholly blind to"
                : "ANY rule that needs one of these axes was not run. Empty results on them describe the " +
                  "reader, not the drawing.";
            return o;
        }

        public static CadReaderCapability FromJson(JObject o)
        {
            if (o == null) return null;
            var c = new CadReaderCapability(o.Value<string>("reader_id") ?? "unknown",
                                            o.Value<string>("reader_version"),
                                            o.Value<string>("reader_description"));
            var axes = o["axes"] as JArray;
            if (axes != null)
                foreach (JToken t in axes)
                {
                    CadAxis a = CadAxis.FromJson(t as JObject);
                    if (a != null) c.Declare(a.Name, a.State, a.Evidence, a.Count);
                }
            return c;
        }
    }

    /// <summary>What kind of drawn thing one IR entity is. Closed, and wider than what any one reader fills.</summary>
    public static class CadEntityKind
    {
        public const string Line = "line";
        public const string Arc = "arc";
        public const string Polyline = "polyline";
        public const string Circle = "circle";
        public const string Spline = "spline";
        public const string Ellipse = "ellipse";
        public const string Point = "point";
        public const string Text = "text";
        public const string BlockInstance = "block_instance";
        public const string Hatch = "hatch";
        public const string Dimension = "dimension";
        public const string Leader = "leader";
        /// <summary>The reader gave something it could not classify. Kept rather than dropped.</summary>
        public const string Unclassified = "unclassified";

        public static readonly string[] All =
        {
            Line, Arc, Polyline, Circle, Spline, Ellipse, Point, Text,
            BlockInstance, Hatch, Dimension, Leader, Unclassified
        };
    }

    /// <summary>One thing the reader found in the drawing.</summary>
    public sealed class CadIrEntity
    {
        /// <summary>Unique within one IR. Assigned by the reader; never reused between readings of different files.</summary>
        public string Id;

        /// <summary>
        /// The DWG's OWN handle, when the reader could reach it.
        ///
        /// Null is the normal case for a reader working through Revit, and null
        /// is not "no handle" - the file has one. It means this reading could not
        /// see it, which is why the IR still works without it and why an IR that
        /// HAS it can do things (exact entity-level diffing between revisions)
        /// that one without it can only approximate.
        /// </summary>
        public string Handle;

        public string Kind = CadEntityKind.Unclassified;
        public string Layer;

        /// <summary>Vertices in millimetres. Two for a line; the ring for a polyline; one for a point or a text anchor.</summary>
        public List<CadPoint> Points = new List<CadPoint>();

        /// <summary>The arc this entity is, when it is one and the reader could give its centre.</summary>
        public CadArcFact Arc;

        /// <summary>The string, for a text entity. Null when the reader is blind to text - check the axis, not this field.</summary>
        public string Text;

        /// <summary>Text height in mm, when known. Distinguishes a room name from a general note.</summary>
        public double? TextHeightMm;

        /// <summary>Rotation in radians, for text and block instances.</summary>
        public double? RotationRadians;

        /// <summary>For a block instance: the definition's name.</summary>
        public string BlockName;

        /// <summary>For a block instance: its attribute tag/value pairs, when the reader can reach them.</summary>
        public Dictionary<string, string> Attributes;

        /// <summary>The nesting path of blocks this entity sits inside, outermost first. Empty at model level.</summary>
        public List<string> BlockPath = new List<string>();

        /// <summary>
        /// WHICH SPACE this entity was drawn in: "model", "paper", or null when
        /// the reader cannot tell (an entity inside a block definition does not
        /// know where the block is placed until something places it).
        ///
        /// It is the difference between a building and a drawing OF a building.
        /// Paper space holds the legend, the title block, the detail bubbles and
        /// the notes - the same blocks, on the same layers, at coordinates that
        /// mean inches on a page. A conversion that cannot tell them apart builds
        /// the legend.
        /// </summary>
        public string Space;

        /// <summary>AutoCAD colour index, when the reader supplies appearance. Null otherwise.</summary>
        public int? ColorIndex;
        public string Linetype;

        /// <summary>True when the reader knows this entity is a chorded approximation of a curve.</summary>
        public bool Approximated;

        /// <summary>Block instance scale, when the reader gives it. A mirrored symbol has a NEGATIVE one.</summary>
        public double? ScaleX, ScaleY;

        /// <summary>Radius in mm, for a circle or an arc read as one.</summary>
        public double? RadiusMm;

        /// <summary>True when a polyline closes. A closed ring is a boundary; an open one is a route.</summary>
        public bool Closed;

        /// <summary>
        /// The file's own type name, for an entity this reading does not model -
        /// HATCH, SPLINE, WIPEOUT, a vertical application's proxy.
        ///
        /// It is kept and counted rather than dropped, because a conversion that
        /// silently ignores a third of a drawing reports full coverage of the part
        /// it understood, and the part it dropped is exactly where somebody's
        /// equipment was.
        /// </summary>
        public string UnmodelledType;

        /// <summary>
        /// For a HATCH: its pattern name and its boundary loops, in millimetres of
        /// the frame it is drawn in (its block's, when it is inside one). Evidence
        /// of where the drafter drew solid material; the fill is not modelled, so
        /// the entity still counts as unmodelled.
        /// </summary>
        public string HatchPattern;
        public List<List<CadPoint>> HatchLoops;
        /// <summary>True when an edge this reading does not decode (ellipse, spline) cut a loop short.</summary>
        public bool HatchLoopsPartial;

        public JObject ToJson()
        {
            var o = new JObject { ["id"] = Id, ["kind"] = Kind };
            if (Handle != null) o["handle"] = Handle;
            if (Layer != null) o["layer"] = Layer;
            if (Points.Count > 0)
                o["points"] = new JArray(Points.Select(p => (JToken)new JArray(
                    Math.Round(p.X, 4, MidpointRounding.AwayFromZero),
                    Math.Round(p.Y, 4, MidpointRounding.AwayFromZero),
                    Math.Round(p.Z, 4, MidpointRounding.AwayFromZero))));
            if (Arc != null)
                o["arc"] = new JObject
                {
                    ["centre"] = new JArray(Math.Round(Arc.Centre.X, 4, MidpointRounding.AwayFromZero),
                                            Math.Round(Arc.Centre.Y, 4, MidpointRounding.AwayFromZero),
                                            Math.Round(Arc.Centre.Z, 4, MidpointRounding.AwayFromZero)),
                    ["radius_mm"] = Math.Round(Arc.RadiusMm, 4, MidpointRounding.AwayFromZero),
                    ["clockwise"] = Arc.Clockwise
                };
            if (Text != null) o["text"] = Text;
            if (TextHeightMm.HasValue) o["text_height_mm"] = Math.Round(TextHeightMm.Value, 3, MidpointRounding.AwayFromZero);
            if (RotationRadians.HasValue) o["rotation_radians"] = Math.Round(RotationRadians.Value, 6, MidpointRounding.AwayFromZero);
            if (BlockName != null) o["block_name"] = BlockName;
            if (Attributes != null && Attributes.Count > 0)
            {
                var a = new JObject();
                foreach (var kv in Attributes.OrderBy(k => k.Key, StringComparer.Ordinal)) a[kv.Key] = kv.Value;
                o["attributes"] = a;
            }
            if (BlockPath.Count > 0) o["block_path"] = new JArray(BlockPath.Select(s => (JToken)s));
            if (ColorIndex.HasValue) o["color_index"] = ColorIndex.Value;
            if (Linetype != null) o["linetype"] = Linetype;
            if (Approximated) o["approximated"] = true;
            if (ScaleX.HasValue) o["scale_x"] = Math.Round(ScaleX.Value, 6, MidpointRounding.AwayFromZero);
            if (ScaleY.HasValue) o["scale_y"] = Math.Round(ScaleY.Value, 6, MidpointRounding.AwayFromZero);
            if (RadiusMm.HasValue) o["radius_mm"] = Math.Round(RadiusMm.Value, 4, MidpointRounding.AwayFromZero);
            if (Closed) o["closed"] = true;
            if (UnmodelledType != null) o["unmodelled_type"] = UnmodelledType;
            return o;
        }
    }

    /// <summary>A layer as the drawing declares it, beside what was actually found on it.</summary>
    public sealed class CadIrLayer
    {
        public string Name;

        /// <summary>
        /// How many IR ENTITIES sit on this layer. The same unit as
        /// <see cref="CadIr.Entities"/>, so these add up to the total.
        /// </summary>
        public int EntityCount;

        /// <summary>
        /// How many PRIMITIVES the reader visited on this layer, when it counts
        /// them. A different unit from EntityCount and deliberately kept apart:
        /// a reader may visit solids, blocks and residue it turns into no entity
        /// at all, and where the two numbers diverge is where the reading lost the
        /// most. -1 when the reader does not count primitives.
        /// </summary>
        public int PrimitiveCount = -1;

        /// <summary>
        /// The AutoCAD colour INDEX, which only a reader that parses the file can
        /// give. Null from a reader that only sees a resolved colour.
        /// </summary>
        public int? ColorIndex;

        /// <summary>
        /// The RESOLVED colour, "r,g,b". A reader working through a host
        /// application gets this and not the index - they are different facts,
        /// and collapsing them would let "colour 7" and "white" be confused.
        /// </summary>
        public string ColorRgb;

        public string Linetype;
        public bool? Frozen;
        public bool? Off;

        public JObject ToJson()
        {
            var o = new JObject { ["name"] = Name, ["entity_count"] = EntityCount };
            if (PrimitiveCount >= 0)
            {
                o["primitive_count"] = PrimitiveCount;
                if (PrimitiveCount != EntityCount)
                    o["counts_differ_means"] =
                        "the reader visited " + PrimitiveCount.ToString(CultureInfo.InvariantCulture) +
                        " primitives on this layer and produced " +
                        EntityCount.ToString(CultureInfo.InvariantCulture) + " entities from them. The " +
                        "difference is what it visited and could not turn into geometry anyone can build " +
                        "from - solids, blocks, residue - and on a layer where it is large, this reading " +
                        "lost most of what is there.";
            }
            if (ColorIndex.HasValue) o["color_index"] = ColorIndex.Value;
            if (ColorRgb != null) o["color_rgb"] = ColorRgb;
            if (Linetype != null) o["linetype"] = Linetype;
            if (Frozen.HasValue) o["frozen"] = Frozen.Value;
            if (Off.HasValue) o["off"] = Off.Value;
            return o;
        }
    }

    /// <summary>An external reference this drawing pulls in.</summary>
    public sealed class CadIrExternalReference
    {
        public string Name;
        public string Path;
        /// <summary>attachment | overlay | unknown</summary>
        public string Attachment = "unknown";
        /// <summary>Whether the reader found the referenced file. Null when it did not look.</summary>
        public bool? Resolved;
        public CadPoint? InsertionPoint;
        public double? Scale;
        public double? RotationRadians;

        public JObject ToJson()
        {
            var o = new JObject { ["name"] = Name, ["attachment"] = Attachment };
            if (Path != null) o["path"] = Path;
            if (Resolved.HasValue) o["resolved"] = Resolved.Value;
            if (InsertionPoint.HasValue)
                o["insertion_mm"] = new JArray(
                    Math.Round(InsertionPoint.Value.X, 4, MidpointRounding.AwayFromZero),
                    Math.Round(InsertionPoint.Value.Y, 4, MidpointRounding.AwayFromZero),
                    Math.Round(InsertionPoint.Value.Z, 4, MidpointRounding.AwayFromZero));
            if (Scale.HasValue) o["scale"] = Math.Round(Scale.Value, 6, MidpointRounding.AwayFromZero);
            if (RotationRadians.HasValue)
                o["rotation_radians"] = Math.Round(RotationRadians.Value, 6, MidpointRounding.AwayFromZero);
            return o;
        }
    }

    /// <summary>
    /// ONE READING OF ONE DRAWING.
    ///
    /// Versioned, because the shape will grow and a model stamped against v1 must
    /// stay readable. Fingerprinted, because the whole point of an incremental
    /// conversion is telling "the drawing changed" from "the reading did".
    /// </summary>
    public sealed class CadIr
    {
        /// <summary>The IR schema this instance is written to. Bump on any change a v1 reader would misread.</summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;

        /// <summary>The file, as the caller named it. Never sent anywhere; carried so a reading can be attributed.</summary>
        public string SourceName;

        /// <summary>SHA-256 of the file's bytes when the caller supplied it. Null when nobody hashed it.</summary>
        public string SourceSha256;

        /// <summary>WHO read it and what they could see. Never null on an IR anything is allowed to consume.</summary>
        public CadReaderCapability Reader;

        /// <summary>The drawing's declared units, when the reader supplies the units axis. Null otherwise.</summary>
        public string DeclaredUnits;

        /// <summary>The factor applied to get millimetres, and where it came from.</summary>
        public double UnitScaleToMm = 1.0;
        public string UnitScaleSource = "assumed_millimetres";

        public List<CadIrLayer> Layers = new List<CadIrLayer>();
        public List<CadIrEntity> Entities = new List<CadIrEntity>();
        public List<CadIrExternalReference> ExternalReferences = new List<CadIrExternalReference>();

        /// <summary>Block definition name -&gt; how many instances of it were found.</summary>
        public Dictionary<string, int> BlockInstanceCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the reader could not turn into entities, with reasons. Never silently dropped.</summary>
        public List<JObject> NotRead = new List<JObject>();

        /// <summary>UTC stamp of the reading, so two IRs of one file can be ordered.</summary>
        public string ReadUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>
        /// Segments, derived from the entities, for the topology rules that only
        /// speak line work.
        ///
        /// AN ARC'S POINTS ARE ITS CHORDS, not its three defining points. A reader
        /// that fills in only start, middle and end leaves every rule downstream
        /// working from a two-chord approximation of a curve - which on any real
        /// radius is an order of magnitude past the tolerance the caller declared.
        /// <see cref="ToArcs"/> is how a rule gets the real curve.
        /// </summary>
        public List<CadSegment> ToSegments()
        {
            var segs = new List<CadSegment>();
            foreach (CadIrEntity e in Entities)
            {
                if (e.Points.Count < 2) continue;
                CadCurveKind kind = e.Kind == CadEntityKind.Arc ? CadCurveKind.Arc
                    : e.Kind == CadEntityKind.Polyline ? CadCurveKind.Polyline
                    : e.Kind == CadEntityKind.Spline ? CadCurveKind.Spline
                    : e.Kind == CadEntityKind.Line ? CadCurveKind.Line
                    : CadCurveKind.Unknown;
                for (int i = 0; i + 1 < e.Points.Count; i++)
                    segs.Add(new CadSegment(e.Points[i], e.Points[i + 1], e.Layer, kind, i, e.Id));
            }
            return segs;
        }

        /// <summary>
        /// The arcs kept AS arcs, for rules that can build a real curve.
        ///
        /// A wall or a pipe built from chords is N straight pieces that no audit
        /// can match back to the one entity the drawing shows, which is why the arc
        /// travels beside its chords rather than instead of them.
        /// </summary>
        public List<CadArcFact> ToArcs() =>
            Entities.Where(e => e.Arc != null).Select(e => e.Arc).ToList();

        /// <summary>
        /// A REFUSAL, when a caller asks this IR for something its reader is blind
        /// to. Returns null when the axis is usable, so a call site reads:
        ///
        ///     JObject no = ir.RefuseIfBlind(CadAxes.Text, "room naming");
        ///     if (no != null) return no;
        /// </summary>
        public JObject RefuseIfBlind(string axis, string whatNeededIt)
        {
            if (Reader == null)
                return new JObject
                {
                    ["refused"] = "reading_declares_no_reader",
                    ["needed_by"] = whatNeededIt,
                    ["means"] = "an IR with no capability declaration cannot be trusted about what it lacks, " +
                                "so nothing that depends on an axis may run against it"
                };
            return Reader.Can(axis) ? null : Reader.RefusalFor(axis, whatNeededIt);
        }

        /// <summary>
        /// The canonical form: every field that changes what this reading MEANS,
        /// in a fixed order, with no timestamps. Two readings of the same file by
        /// the same reader fingerprint the same; a reading by a better reader does
        /// not, which is correct - it is a different reading.
        /// </summary>
        /// <summary>
        /// ASCII unit separator. It cannot occur in a layer name, a handle, a path
        /// or a drawing's text, which is the whole reason it is here.
        ///
        /// THE FIRST VERSION USED A VERTICAL BAR, and a vertical bar occurs in all
        /// four. Two DIFFERENT readings could then canonicalise to the same string
        /// - a layer called "A|B" with no text against a layer called "A" whose
        /// text is "B" - and fingerprint the same, which an incremental run reads
        /// as "the drawing has not changed". CadIdentity has used this separator
        /// for exactly this reason since long before the IR existed.
        /// </summary>
        private const char Sep = '\u001f';

        public string Canonical()
        {
            var sb = new StringBuilder();
            sb.Append("ir/v").Append(SchemaVersion).Append('\n');
            sb.Append("source=").Append(SourceName ?? "").Append('\n');
            sb.Append("sha=").Append(SourceSha256 ?? "").Append('\n');
            sb.Append("reader=").Append(Reader == null ? "" : Reader.ReaderId + Sep + Reader.ReaderVersion).Append('\n');
            sb.Append("units=").Append(DeclaredUnits ?? "").Append(Sep)
              .Append(UnitScaleToMm.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            foreach (CadIrLayer l in Layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
                sb.Append("layer=").Append(l.Name).Append(Sep).Append(l.EntityCount).Append('\n');
            foreach (CadIrEntity e in Entities.OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                sb.Append("e=").Append(e.Id).Append(Sep).Append(e.Kind).Append(Sep).Append(e.Layer ?? "")
                  .Append(Sep).Append(e.Handle ?? "").Append(Sep).Append(e.Text ?? "").Append(Sep);
                foreach (CadPoint p in e.Points)
                    sb.Append(p.X.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.Y.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.Z.ToString("0.####", CultureInfo.InvariantCulture)).Append(';');
                sb.Append('\n');
            }
            foreach (CadIrExternalReference x in ExternalReferences.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                sb.Append("xref=").Append(x.Name).Append(Sep).Append(x.Path ?? "").Append('\n');
            return sb.ToString();
        }

        /// <summary>SHA-256 over <see cref="Canonical"/>. The handle an incremental run compares by.</summary>
        public string Fingerprint()
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical()));
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>The census: what this reading contains, without the entities themselves.</summary>
        public JObject SummaryJson()
        {
            var byKind = new JObject();
            foreach (var g in Entities.GroupBy(e => e.Kind, StringComparer.Ordinal)
                                      .OrderByDescending(g => g.Count()))
                byKind[g.Key] = g.Count();

            return new JObject
            {
                ["schema_version"] = SchemaVersion,
                ["source_name"] = SourceName,
                ["source_sha256"] = SourceSha256,
                ["read_utc"] = ReadUtc,
                ["fingerprint"] = Fingerprint(),
                ["reader"] = Reader == null ? (JToken)JValue.CreateNull() : Reader.ToJson(),
                ["units"] = new JObject
                {
                    ["declared"] = DeclaredUnits,
                    ["scale_to_mm"] = UnitScaleToMm,
                    ["scale_source"] = UnitScaleSource
                },
                ["entity_count"] = Entities.Count,
                ["entities_by_kind"] = byKind,
                ["layer_count"] = Layers.Count,
                ["layers"] = new JArray(Layers
                    .OrderByDescending(l => l.EntityCount)
                    .Select(l => (JToken)l.ToJson())),
                ["external_references"] = new JArray(ExternalReferences.Select(x => (JToken)x.ToJson())),
                ["block_instance_counts"] = BlockCountsJson(),
                ["not_read"] = new JArray(NotRead),
                ["counts_mean"] = CountsCaveat()
            };
        }

        private JObject BlockCountsJson()
        {
            var o = new JObject();
            foreach (var kv in BlockInstanceCounts.OrderByDescending(k => k.Value)) o[kv.Key] = kv.Value;
            return o;
        }

        /// <summary>
        /// The sentence that must travel with every count this IR publishes.
        ///
        /// A census produced by a reader blind to half the file is a census of
        /// what that reader saw, and reporting it as a census of the drawing is
        /// the single easiest way to mislead somebody with true numbers.
        /// </summary>
        public string CountsCaveat()
        {
            if (Reader == null) return "no reader declared: treat every count as unattributable";
            var blind = Reader.Blind.Select(a => a.Name).ToList();
            var partial = CadAxes.All.Select(Reader.Axis)
                                     .Where(a => a != null && a.State == CadAxisState.Partial)
                                     .Select(a => a.Name).ToList();
            if (blind.Count == 0 && partial.Count == 0)
                return "every axis this reader declared was supplied in full; the counts are the drawing's";
            var sb = new StringBuilder("THESE COUNTS DESCRIBE WHAT ").Append(Reader.ReaderId).Append(" COULD REACH. ");
            if (blind.Count > 0)
                sb.Append("It is blind to: ").Append(string.Join(", ", blind))
                  .Append(" - zero on any of those is this reader, not the drawing. ");
            if (partial.Count > 0)
                sb.Append("It supplied only part of: ").Append(string.Join(", ", partial))
                  .Append(" - counts there are lower bounds.");
            return sb.ToString().TrimEnd();
        }

        public JObject ToJson()
        {
            JObject o = SummaryJson();
            o["entities"] = new JArray(Entities.Select(e => (JToken)e.ToJson()));
            return o;
        }
    }
}
