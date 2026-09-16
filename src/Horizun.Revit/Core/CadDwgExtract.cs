// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// THE PART OF A DWG THAT REVIT THROWS AWAY.
//
// A CAD import into Revit arrives as geometry and nothing else: no string, no
// block name, no attribute, no entity handle, no external reference. That was
// measured, not assumed, and the IR says so by declaring those axes unavailable.
// Everything a conversion needs in order to be TRACEABLE rather than merely
// plausible lives in exactly those fields - what a symbol is, what a unit is
// called, which drawing a line came from, which entity it was.
//
// This file is the parser for a reading that HAS them. The extraction itself is
// done by accoreconsole, the headless AutoCAD that ships with the AutoCAD the
// machine already has; this half takes its report and turns it into the IR, and
// it is deliberately pure - no process, no file system, no Revit - so that every
// shape it can meet is testable without any of the three.
//
// THE FORMAT IS TAB-SEPARATED, NOT JSON. AutoLISP has no escaping worth trusting
// and a drawing's text is full of quotes and backslashes; the extractor escapes
// four characters and this unescapes them, which is a contract small enough to
// hold in your head.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What one run of the extractor found, before it becomes an IR.</summary>
    public sealed class CadDwgReading
    {
        public string DrawingName;

        /// <summary>The DWG's own INSUNITS code, or null when the file declares none.</summary>
        public int? InsUnits;
        public int? Measurement;

        /// <summary>Millimetres per drawing unit, derived from INSUNITS. Null when INSUNITS is 0 - unitless.</summary>
        public double? MmPerUnit;

        public CadPoint? ExtMin, ExtMax;

        public List<CadIrLayer> Layers = new List<CadIrLayer>();
        public List<CadIrExternalReference> ExternalReferences = new List<CadIrExternalReference>();
        public List<CadIrEntity> Entities = new List<CadIrEntity>();

        /// <summary>Block definition names, including the ones that are external references.</summary>
        public List<string> BlockNames = new List<string>();

        /// <summary>True when the extractor wrote its end marker. A report without it was cut off.</summary>
        public bool Complete;

        /// <summary>Lines the parser did not understand, with their line numbers. Never silently dropped.</summary>
        public List<string> Unparsed = new List<string>();

        /// <summary>Entity kinds seen, with counts - including the ones this reading does not model.</summary>
        public Dictionary<string, int> RawTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public static class CadDwgExtract
    {
        public const string ReaderId = "autocad-accoreconsole";

        /// <summary>
        /// Millimetres per drawing unit for an INSUNITS code.
        ///
        /// 0 is UNITLESS and returns null rather than 1: a drawing that declares no
        /// unit is not a drawing in millimetres, and treating it as one is how a
        /// building gets built at 1/25th scale. The caller has to say what it is.
        /// </summary>
        public static double? MmPerUnitOf(int insUnits)
        {
            switch (insUnits)
            {
                case 1: return 25.4;            // inches
                case 2: return 304.8;           // feet
                case 4: return 1.0;             // millimetres
                case 5: return 10.0;            // centimetres
                case 6: return 1000.0;          // metres
                case 8: return 0.0254;          // microinches
                case 9: return 25.4e-3;         // mils
                case 10: return 914.4;          // yards
                case 13: return 1e-6;           // nanometres
                case 14: return 0.1;            // decimetres
                case 15: return 10000.0;        // decametres
                case 16: return 100000.0;       // hectometres
                case 17: return 1e9;            // gigametres
                default: return null;           // 0 unitless, and everything unmapped
            }
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\\') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            return sb.ToString();
        }

        private static double? Num(string s)
        {
            double d;
            if (string.IsNullOrWhiteSpace(s)) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (double?)d : null;
        }

        private static CadPoint? Point(string[] f, int at, double mm)
        {
            if (f.Length <= at + 2) return null;
            double? x = Num(f[at]), y = Num(f[at + 1]), z = Num(f[at + 2]);
            if (!x.HasValue || !y.HasValue) return null;
            return new CadPoint(x.Value * mm, y.Value * mm, (z ?? 0) * mm);
        }

        /// <summary>
        /// The extractor's report, parsed.
        ///
        /// <paramref name="assumeMmPerUnit"/> is used ONLY when the drawing declares
        /// INSUNITS 0 - unitless - and a caller has said what it is. It is not a
        /// default and it never overrides a declared unit: a drawing that says
        /// inches is in inches even if the caller believes otherwise, and the
        /// disagreement is the caller's to resolve before converting anything.
        /// </summary>
        public static CadDwgReading Parse(IEnumerable<string> lines, double? assumeMmPerUnit = null)
        {
            var r = new CadDwgReading();
            if (lines == null) return r;

            // The unit is needed to scale every coordinate, and it arrives in the
            // header before any entity - but a report is a stream and this does not
            // assume ordering. Entities are held raw and scaled at the end.
            var raw = new List<string[]>();
            var attribs = new List<string[]>();
            int lineNo = 0;

            foreach (string line in lines)
            {
                lineNo++;
                if (line == null) continue;
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "H":
                        if (f.Length < 3) { r.Unparsed.Add(lineNo + ": " + line); break; }
                        switch (f[1])
                        {
                            case "dwg": r.DrawingName = Unescape(f[2]); break;
                            case "insunits": { double? v = Num(f[2]); if (v.HasValue) r.InsUnits = (int)v.Value; break; }
                            case "measurement": { double? v = Num(f[2]); if (v.HasValue) r.Measurement = (int)v.Value; break; }
                            case "done": r.Complete = true; break;
                            case "extmin": case "extmax": break;   // scaled below, once the unit is known
                        }
                        if (f[1] == "extmin" || f[1] == "extmax") raw.Add(f);
                        break;

                    case "L":
                        if (f.Length < 3) { r.Unparsed.Add(lineNo + ": " + line); break; }
                        r.Layers.Add(new CadIrLayer
                        {
                            Name = Unescape(f[1]),
                            ColorIndex = (int?)Num(f[2]),
                            Linetype = f.Length > 3 ? Unescape(f[3]) : null,
                            Frozen = ((int)(Num(f.Length > 4 ? f[4] : null) ?? 0) & 1) != 0
                        });
                        break;

                    case "K":
                        if (f.Length < 2) { r.Unparsed.Add(lineNo + ": " + line); break; }
                        string bname = Unescape(f[1]);
                        r.BlockNames.Add(bname);
                        string path = f.Length > 3 ? Unescape(f[3]) : "";
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            int flags = (int)(Num(f.Length > 2 ? f[2] : null) ?? 0);
                            r.ExternalReferences.Add(new CadIrExternalReference
                            {
                                Name = bname,
                                Path = path,
                                // Bit 4 is "is an xref", bit 8 "is an overlay".
                                Attachment = (flags & 8) != 0 ? "overlay" : "attachment",
                                // A reference AutoCAD could not find is loaded as an
                                // unresolved placeholder and bit 32 stays clear.
                                Resolved = (flags & 32) != 0
                            });
                        }
                        break;

                    case "E": raw.Add(f); break;
                    case "A": attribs.Add(f); break;
                    default: r.Unparsed.Add(lineNo + ": " + line); break;
                }
            }

            double? declared = r.InsUnits.HasValue ? MmPerUnitOf(r.InsUnits.Value) : null;
            r.MmPerUnit = declared ?? assumeMmPerUnit;
            double mm = r.MmPerUnit ?? 1.0;

            // Attributes, by the handle of the INSERT that owns them.
            var byOwner = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (string[] a in attribs)
            {
                if (a.Length < 4) continue;
                string owner = a[1];
                Dictionary<string, string> bag;
                if (!byOwner.TryGetValue(owner, out bag)) byOwner[owner] = bag = new Dictionary<string, string>(StringComparer.Ordinal);
                bag[Unescape(a[2])] = Unescape(a[3]);
            }

            int n = 0;
            foreach (string[] f in raw)
            {
                if (f[0] == "H")
                {
                    CadPoint? p = Point(f, 2, mm);
                    if (f[1] == "extmin") r.ExtMin = p; else r.ExtMax = p;
                    continue;
                }
                if (f.Length < 5) { r.Unparsed.Add("entity with too few fields: " + string.Join("|", f)); continue; }

                string owner = f[1], handle = f[2], type = f[3], layer = Unescape(f[4]);
                Bump(r.RawTypeCounts, type);

                var e = new CadIrEntity
                {
                    Id = "e" + (++n).ToString(CultureInfo.InvariantCulture),
                    Handle = string.IsNullOrWhiteSpace(handle) ? null : handle,
                    Layer = layer
                };
                // The owning block record, unless it is a space. The path is one deep
                // because that is what this walk can prove: an entity inside a block
                // knows its block, and the INSERT that placed that block is a
                // different record in the same report.
                if (!string.IsNullOrWhiteSpace(owner) &&
                    !owner.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) &&
                    !owner.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase))
                    e.BlockPath.Add(owner);

                // WHICH SPACE, kept rather than flattened. Both spaces used to
                // produce an empty block path, and an empty path reads as "the
                // drawing itself" - so every symbol on every SHEET arrived as a
                // thing to build. An entity inside a block definition keeps null:
                // it is in whatever space places its block, which this record
                // cannot see.
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    if (owner.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase)) e.Space = "model";
                    else if (owner.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) e.Space = "paper";
                }

                switch (type)
                {
                    case "TEXT":
                    case "MTEXT":
                        e.Kind = CadEntityKind.Text;
                        AddPoint(e, Point(f, 5, mm));
                        e.TextHeightMm = Scale(Num(Field(f, 8)), mm);
                        e.RotationRadians = Num(Field(f, 9));
                        e.Text = Unescape(Field(f, 10));
                        break;

                    case "ATTDEF":
                        e.Kind = CadEntityKind.Text;
                        AddPoint(e, Point(f, 5, mm));
                        e.TextHeightMm = Scale(Num(Field(f, 8)), mm);
                        e.RotationRadians = Num(Field(f, 9));
                        e.BlockName = Unescape(Field(f, 10));       // the TAG
                        e.Text = Unescape(Field(f, 11));
                        break;

                    case "INSERT":
                        e.Kind = CadEntityKind.BlockInstance;
                        AddPoint(e, Point(f, 5, mm));
                        e.RotationRadians = Num(Field(f, 8));
                        e.BlockName = Unescape(Field(f, 12));
                        double? sx = Num(Field(f, 9)), sy = Num(Field(f, 10));
                        if (sx.HasValue) e.ScaleX = sx;
                        if (sy.HasValue) e.ScaleY = sy;
                        Dictionary<string, string> bag2;
                        if (handle != null && byOwner.TryGetValue(handle, out bag2)) e.Attributes = bag2;
                        break;

                    case "LINE":
                        e.Kind = CadEntityKind.Line;
                        AddPoint(e, Point(f, 5, mm));
                        AddPoint(e, Point(f, 8, mm));
                        break;

                    case "CIRCLE":
                        e.Kind = CadEntityKind.Circle;
                        AddPoint(e, Point(f, 5, mm));
                        e.RadiusMm = Scale(Num(Field(f, 8)), mm);
                        break;

                    case "ARC":
                    {
                        e.Kind = CadEntityKind.Arc;
                        CadPoint? c = Point(f, 5, mm);
                        double? rad = Scale(Num(Field(f, 8)), mm);
                        double? a1 = Num(Field(f, 9)), a2 = Num(Field(f, 10));
                        AddPoint(e, c);
                        e.RadiusMm = rad;
                        if (c.HasValue && rad.HasValue && a1.HasValue && a2.HasValue)
                        {
                            double r1 = a1.Value * Math.PI / 180.0, r2 = a2.Value * Math.PI / 180.0;
                            var start = new CadPoint(c.Value.X + rad.Value * Math.Cos(r1),
                                                     c.Value.Y + rad.Value * Math.Sin(r1), c.Value.Z);
                            var end = new CadPoint(c.Value.X + rad.Value * Math.Cos(r2),
                                                   c.Value.Y + rad.Value * Math.Sin(r2), c.Value.Z);
                            // DWG arcs run COUNTER-CLOCKWISE from start to end, always -
                            // that is the format, not a convention this reading chose.
                            // The middle point is what CadArcFact identifies an arc by,
                            // and it is the only thing that separates the minor arc from
                            // the major one between the same two ends.
                            double half = r1 + Sweep(r1, r2) / 2.0;
                            var middle = new CadPoint(c.Value.X + rad.Value * Math.Cos(half),
                                                      c.Value.Y + rad.Value * Math.Sin(half), c.Value.Z);
                            e.Arc = new CadArcFact(e.Handle ?? e.Id, c.Value, rad.Value, start, end,
                                                   middle, layer, 0, 0.0);
                        }
                        break;
                    }

                    case "LWPOLYLINE":
                    {
                        e.Kind = CadEntityKind.Polyline;
                        string pts = Field(f, 7);
                        if (!string.IsNullOrWhiteSpace(pts))
                            foreach (string v in pts.Split(';'))
                            {
                                string[] xy = v.Split(',');
                                if (xy.Length < 2) continue;
                                double? x = Num(xy[0]), y = Num(xy[1]);
                                if (x.HasValue && y.HasValue) e.Points.Add(new CadPoint(x.Value * mm, y.Value * mm, 0));
                            }
                        double? closed = Num(Field(f, 6));
                        e.Closed = closed.HasValue && ((int)closed.Value & 1) != 0;
                        break;
                    }

                    case "VERTEX":
                        e.Kind = CadEntityKind.Point;
                        AddPoint(e, Point(f, 5, mm));
                        break;

                    case "SEQEND":
                    case "ENDBLK":
                        continue;   // structural markers, not drawn things

                    default:
                        // NAMED, NOT DROPPED. A hatch, a spline, a wipeout, a proxy:
                        // this reading does not model their geometry and says which
                        // ones it met, because a conversion that silently ignores a
                        // third of a drawing reports full coverage of the part it
                        // understood.
                        e.Kind = CadEntityKind.Unclassified;
                        e.Text = null;
                        e.UnmodelledType = type;
                        AddPoint(e, Point(f, 5, mm));
                        break;
                }
                r.Entities.Add(e);
            }

            return r;
        }

        private static double Sweep(double a1, double a2)
        {
            double d = a2 - a1;
            while (d <= 0) d += 2 * Math.PI;
            while (d > 2 * Math.PI) d -= 2 * Math.PI;
            return d;
        }

        private static void AddPoint(CadIrEntity e, CadPoint? p) { if (p.HasValue) e.Points.Add(p.Value); }
        private static double? Scale(double? v, double mm) { return v.HasValue ? (double?)(v.Value * mm) : null; }
        private static string Field(string[] f, int i) { return i < f.Length ? f[i] : null; }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            int c;
            d[k] = d.TryGetValue(k, out c) ? c + 1 : 1;
        }

        /// <summary>
        /// What this reader can and cannot answer, per axis, said in the IR's own
        /// vocabulary and derived from what the report ACTUALLY contained rather
        /// than from what the reader hopes to do.
        ///
        /// The difference between Absent and Unavailable is the whole point: a
        /// drawing with no text and a reader that cannot see text produce the same
        /// empty list and mean opposite things.
        /// </summary>
        public static CadReaderCapability Capability(CadDwgReading r, string acadVersion)
        {
            var cap = new CadReaderCapability(ReaderId, acadVersion ?? "unknown",
                "the headless AutoCAD console reading the file itself, so the fields Revit's import drops - " +
                "text, block names, attributes, entity handles, external references, the declared unit - are " +
                "read from the DWG rather than inferred");

            int texts = r.Entities.Count(e => e.Kind == CadEntityKind.Text && e.Text != null);
            int blocks = r.Entities.Count(e => e.Kind == CadEntityKind.BlockInstance);
            int attrs = r.Entities.Count(e => e.Attributes != null && e.Attributes.Count > 0);
            int handles = r.Entities.Count(e => e.Handle != null);
            int geometry = r.Entities.Count(e => e.Kind == CadEntityKind.Line || e.Kind == CadEntityKind.Arc ||
                                                 e.Kind == CadEntityKind.Polyline || e.Kind == CadEntityKind.Circle);
            int unmodelled = r.Entities.Count(e => e.UnmodelledType != null);

            cap.Declare(CadAxes.Geometry,
                unmodelled > 0 ? CadAxisState.Partial : CadAxisState.Supplied,
                unmodelled > 0
                    ? "lines, arcs, circles and polylines are read as geometry; " + unmodelled +
                      " entities of types this reading does not model (hatches, splines, solids, proxies) are " +
                      "listed by type and carry only their anchor point"
                    : "every entity met was of a type this reading models",
                geometry);

            cap.Declare(CadAxes.Layers, r.Layers.Count > 0 ? CadAxisState.Supplied : CadAxisState.Absent,
                r.Layers.Count > 0
                    ? "the layer table was read from the file"
                    : "the file's layer table came back empty, which no real drawing is - treat this reading as suspect",
                r.Layers.Count);

            cap.Declare(CadAxes.EntityHandles, handles > 0 ? CadAxisState.Supplied : CadAxisState.Absent,
                handles > 0
                    ? "every entity carries the DWG's own handle, so a revision can be diffed entity by entity"
                    : "no entity reported a handle", handles);

            cap.Declare(CadAxes.Text,
                texts > 0 ? CadAxisState.Supplied : CadAxisState.Absent,
                texts > 0
                    ? "TEXT, MTEXT and attribute definitions were read with their contents, heights and rotations"
                    : "this reader reads text and THIS DRAWING carries none in the spaces walked - which is a " +
                      "finding about the drawing, not a limit of the reading",
                texts);

            cap.Declare(CadAxes.BlockNames, r.BlockNames.Count > 0 ? CadAxisState.Supplied : CadAxisState.Absent,
                r.BlockNames.Count > 0
                    ? "the block table was read, so a symbol is identified by the name its author gave it"
                    : "the drawing defines no blocks", r.BlockNames.Count);

            cap.Declare(CadAxes.BlockAttributes, attrs > 0 ? CadAxisState.Supplied : CadAxisState.Absent,
                attrs > 0
                    ? "attribute tags and values were read from the instances that carry them"
                    : "no block instance in this drawing carries attribute values", attrs);

            cap.Declare(CadAxes.ExternalReferences,
                r.ExternalReferences.Count > 0 ? CadAxisState.Supplied : CadAxisState.Absent,
                r.ExternalReferences.Count > 0
                    ? "the block table names every external reference with its stored path and whether AutoCAD " +
                      "resolved it when the file was opened"
                    : "this drawing references no other file", r.ExternalReferences.Count);

            cap.Declare(CadAxes.Units,
                r.InsUnits.HasValue && r.MmPerUnit.HasValue ? CadAxisState.Supplied : CadAxisState.Absent,
                r.InsUnits.HasValue && r.MmPerUnit.HasValue
                    ? "INSUNITS " + r.InsUnits.Value.ToString(CultureInfo.InvariantCulture) + ", so one drawing " +
                      "unit is " + r.MmPerUnit.Value.ToString("0.####", CultureInfo.InvariantCulture) + " mm"
                    : "the drawing declares INSUNITS 0 - UNITLESS. Nothing here knows what a unit means and " +
                      "every length below is in drawing units, unscaled",
                r.InsUnits ?? 0);

            // WHAT THIS READER STILL CANNOT DO, said rather than left undeclared -
            // an axis nobody mentions is treated as incapable, which would be the
            // right answer for the wrong reason.
            cap.Declare(CadAxes.Layouts, CadAxisState.Partial,
                "the walk covers every block record, which includes each layout's paper space, but this " +
                "reading does not carry the layouts' viewports, their scales or which model-space region each " +
                "one shows", 0);

            cap.Declare(CadAxes.Elevation, CadAxisState.Partial,
                "every point carries the Z the file stores, which on a plan drawing is zero almost everywhere. " +
                "A height that exists only as TEXT is in the text axis and has not been associated with anything",
                0);

            cap.Declare(CadAxes.Appearance,
                r.Layers.Any(l => l.ColorIndex.HasValue) ? CadAxisState.Partial : CadAxisState.Unavailable,
                "layer colour index and linetype name are read; per-entity overrides, plot styles, lineweights " +
                "and transparency are not", r.Layers.Count);

            cap.Declare(CadAxes.ExtendedData, CadAxisState.Unavailable,
                "XDATA and extension dictionaries are not read by this extraction. They are where a vertical " +
                "application keeps what it knows, so a drawing produced by one carries meaning this reading " +
                "cannot see", 0);

            return cap;
        }
    }
}
