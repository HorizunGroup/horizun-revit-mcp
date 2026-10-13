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
        /// <summary>
        /// Each block definition's BASE POINT, in mm: what its contents are placed relative to.
        /// Absent from extractions made before it was recorded, which read as the origin.
        /// </summary>
        public Dictionary<string, CadPoint> BlockBases = new Dictionary<string, CadPoint>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when the extractor wrote its end marker. A report without it was cut off.</summary>
        public bool Complete;

        /// <summary>Lines the parser did not understand, with their line numbers. Never silently dropped.</summary>
        public List<string> Unparsed = new List<string>();

        /// <summary>Top-level rows whose handle had already been read - a space walk running on through the drawing.</summary>
        public int RepeatedTopLevelRows;

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
        /// <summary>
        /// An entity written in its own OCS (extrusion not +Z), put into world coordinates
        /// (audit 2026-10-08, lote 7). ARC, CIRCLE, LWPOLYLINE, INSERT and TEXT carry OCS
        /// coordinates; MTEXT's insertion point is already world and is left alone. A
        /// tilted extrusion has no plan angle: its points are converted and the reading
        /// names it.
        /// </summary>
        private static void ToWorld(CadIrEntity e, string type, double[] n, CadDwgReading r)
        {
            if (type != "ARC" && type != "CIRCLE" && type != "LWPOLYLINE" && type != "INSERT" && type != "TEXT") return;
            double nx = n[0], ny = n[1], nz = n[2];
            for (int i = 0; i < e.Points.Count; i++) e.Points[i] = CadOcs.ToWorld(e.Points[i], nx, ny, nz);
            if (e.TextAlignmentPoint.HasValue) e.TextAlignmentPoint = CadOcs.ToWorld(e.TextAlignmentPoint.Value, nx, ny, nz);
            if (e.Arc != null)
                e.Arc = new CadArcFact(e.Arc.CurveId, CadOcs.ToWorld(e.Arc.Centre, nx, ny, nz), e.Arc.RadiusMm,
                                       CadOcs.ToWorld(e.Arc.Start, nx, ny, nz), CadOcs.ToWorld(e.Arc.End, nx, ny, nz),
                                       CadOcs.ToWorld(e.Arc.Middle, nx, ny, nz), e.Arc.Layer, e.Arc.ChordCount, e.Arc.SagittaMm);
            bool plan = CadOcs.IsMirroredPlan(nx, ny, nz);
            if (type == "INSERT")
            {
                if (plan)
                {
                    // A block reflected about world Y: in the placement's own terms (scale,
                    // reflection about the block's Y axis, then the turn) the X scale changes
                    // sign and the turn reverses.
                    e.ScaleX = -(e.ScaleX ?? 1.0);
                    double turn = -(e.RotationRadians ?? 0.0);
                    turn %= 2 * Math.PI; if (turn < 0) turn += 2 * Math.PI;
                    e.RotationRadians = turn;
                }
                else r.Unparsed.Add("INSERT " + e.Handle + " has a tilted extrusion (" + Fmt(nx) + "," + Fmt(ny) + "," + Fmt(nz) +
                                    "): its insertion point is converted, its rotation has no plan reading and is left as written.");
            }
            else if (type == "TEXT" && e.RotationRadians.HasValue)
            {
                double? world = CadOcs.DirectionToWorld(e.RotationRadians.Value, nx, ny, nz);
                if (world.HasValue) e.RotationRadians = world;
                else r.Unparsed.Add("TEXT " + e.Handle + " has a tilted extrusion: its rotation has no plan reading and is left as written.");
            }
        }

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        public static double? MmPerUnitOf(int insUnits)
        {
            // THE DXF REFERENCE'S TABLE, every code (audit 2026-10-08, lote 7): 8 is
            // MICROINCHES and 13 MICRONS (they read 0.0254 and 1e-6, the values of mils and
            // nanometres), 14 decimetres read 0.1 instead of 100, 17 gigametres 1e9 instead of
            // 1e12. US survey units are 1200/3937 m to the foot.
            const double SurveyFootMm = 1200.0 / 3937.0 * 1000.0;
            switch (insUnits)
            {
                case 1: return 25.4;                    // inches
                case 2: return 304.8;                   // feet
                case 3: return 1609344.0;               // miles
                case 4: return 1.0;                     // millimetres
                case 5: return 10.0;                    // centimetres
                case 6: return 1000.0;                  // metres
                case 7: return 1e6;                     // kilometres
                case 8: return 2.54e-5;                 // microinches
                case 9: return 0.0254;                  // mils
                case 10: return 914.4;                  // yards
                case 11: return 1e-7;                   // angstroms
                case 12: return 1e-6;                   // nanometres
                case 13: return 1e-3;                   // microns
                case 14: return 100.0;                  // decimetres
                case 15: return 10000.0;                // decametres
                case 16: return 100000.0;               // hectometres
                case 17: return 1e12;                   // gigametres
                case 18: return 1.495978707e14;         // astronomical units
                case 19: return 9.4607304725808e18;     // light years
                case 20: return 3.0856775814913673e19;  // parsecs
                case 21: return SurveyFootMm;           // US survey feet
                case 22: return SurveyFootMm / 12.0;    // US survey inches
                case 23: return SurveyFootMm * 3.0;     // US survey yards
                case 24: return SurveyFootMm * 5280.0;  // US survey miles
                default: return null;                   // 0 unitless, and everything unmapped
            }
        }

        /// <summary>
        /// The XY diagonal of $EXTMIN..$EXTMAX in the DRAWING'S OWN units - what a link's scale is measured
        /// against (CadLinkUnitRules). The reading stores the extents already multiplied by MmPerUnit (1 when
        /// the drawing is unitless), so that factor is divided back out. Null when either corner is missing,
        /// inverted, or AutoCAD's empty-drawing sentinel (+/-1e20): an empty extent measures nothing.
        /// </summary>
        public static double? DrawingDiagonalUnits(CadDwgReading r)
        {
            if (r == null || !r.ExtMin.HasValue || !r.ExtMax.HasValue) return null;
            double s = r.MmPerUnit ?? 1.0;
            if (s <= 0) return null;
            CadPoint a = r.ExtMin.Value, b = r.ExtMax.Value;
            foreach (double v in new[] { a.X, a.Y, b.X, b.Y })
                if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v / s) >= 1e19) return null;
            double dx = (b.X - a.X) / s, dy = (b.Y - a.Y) / s;
            if (dx < 0 || dy < 0) return null;
            double d = Math.Sqrt(dx * dx + dy * dy);
            return d > 0 ? (double?)d : null;
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

        /// <summary>
        /// The entity's own colour (62) and linetype (6), written by the extractor after the
        /// geometry (absent from dumps made before them). Kept raw here and resolved against the
        /// layer once every layer is read: BYLAYER takes the layer's, BYBLOCK (colour 0) is left
        /// unknown - the block reference decides it, and this record does not see it.
        /// </summary>
        private static void Appearance(CadIrEntity e, string[] f, int at, Dictionary<CadIrEntity, string[]> raw)
        {
            if (f.Length <= at) return;
            raw[e] = new[] { Field(f, at), Field(f, at + 1), Field(f, at + 2) };
        }

        private static void ResolveAppearance(CadDwgReading r, Dictionary<CadIrEntity, string[]> raw)
        {
            var layers = new Dictionary<string, CadIrLayer>(StringComparer.OrdinalIgnoreCase);
            foreach (CadIrLayer l in r.Layers) if (l.Name != null && !layers.ContainsKey(l.Name)) layers[l.Name] = l;
            foreach (KeyValuePair<CadIrEntity, string[]> kv in raw)
            {
                CadIrEntity e = kv.Key;
                layers.TryGetValue(e.Layer ?? "", out CadIrLayer layer);
                double? c = Num(kv.Value[0]);
                int code = c.HasValue ? (int)Math.Round(c.Value) : 256;
                e.ColorIndex = code == 256 ? (layer?.ColorIndex.HasValue == true ? (int?)Math.Abs(layer.ColorIndex.Value) : null)
                             : code == 0 ? null : (int?)code;
                double? tc = Num(kv.Value[2]);
                if (tc.HasValue && tc.Value >= 0 && tc.Value <= 0xFFFFFF)
                    e.TrueColor = "#" + ((int)tc.Value).ToString("x6", CultureInfo.InvariantCulture);
                string lt = Unescape(kv.Value[1]);
                e.Linetype = string.IsNullOrWhiteSpace(lt) || string.Equals(lt, "BYLAYER", StringComparison.OrdinalIgnoreCase) ? layer?.Linetype
                           : string.Equals(lt, "BYBLOCK", StringComparison.OrdinalIgnoreCase) ? null : lt;
            }
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
            var rawBases = new List<string[]>();
            var normals = new Dictionary<string, double[]>(StringComparer.Ordinal);
            var appearance = new Dictionary<CadIrEntity, string[]>();
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
                        if (f.Length >= 7) rawBases.Add(new[] { bname, f[4], f[5], f[6] });
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
                    case "X":
                    {
                        // An entity's extrusion direction (group 210), written only when it is not +Z.
                        double? nx = Num(f.Length > 2 ? f[2] : null), ny = Num(f.Length > 3 ? f[3] : null),
                                nz = Num(f.Length > 4 ? f[4] : null);
                        if (f.Length > 1 && !string.IsNullOrEmpty(f[1]) && nx.HasValue && ny.HasValue && nz.HasValue)
                            normals[f[1]] = new[] { nx.Value, ny.Value, nz.Value };
                        else r.Unparsed.Add(lineNo + ": " + line);
                        break;
                    }
                    default: r.Unparsed.Add(lineNo + ": " + line); break;
                }
            }

            double? declared = r.InsUnits.HasValue ? MmPerUnitOf(r.InsUnits.Value) : null;
            r.MmPerUnit = declared ?? assumeMmPerUnit;
            double mm = r.MmPerUnit ?? 1.0;
            foreach (string[] b in rawBases)
            {
                double? bx = Num(b[1]), by = Num(b[2]), bz = Num(b[3]);
                if (bx.HasValue && by.HasValue) r.BlockBases[b[0]] = new CadPoint(bx.Value * mm, by.Value * mm, (bz ?? 0) * mm);
            }

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

            // WHAT EACH DEFINITION DRAWS, fingerprinted. Two instances of one dynamic
            // block with different parameter values reference different anonymous
            // definitions; their names are renumbered on every edit, their CONTENT is
            // what separates the variants. Handles are left out (they are per copy).
            Dictionary<string, string> definitionSignature = DefinitionSignatures(raw);

            int n = 0;
            var topLevelHandles = new HashSet<string>(StringComparer.Ordinal);
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
                bool topLevel = owner.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) ||
                                owner.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase);
                // ONE ROW PER ENTITY. Each space walk can run on through the whole
                // drawing, so the same top-level handle may arrive more than once;
                // the first row carries its own space, read from the entity.
                if (topLevel && !string.IsNullOrWhiteSpace(handle) && !topLevelHandles.Add(handle))
                {
                    r.RepeatedTopLevelRows++;
                    continue;
                }
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
                        // What places the point (fields 11.., absent from dumps made before they
                        // were recorded, which leaves TextType null - not "left justified").
                        if (f.Length > 11)
                        {
                            e.TextType = type;
                            if (type == "TEXT")
                            {
                                double? jh = Num(Field(f, 11)), jv = Num(Field(f, 12));
                                e.TextJustifyH = jh.HasValue ? (int?)(int)Math.Round(jh.Value) : 0;
                                e.TextJustifyV = jv.HasValue ? (int?)(int)Math.Round(jv.Value) : 0;
                                e.TextAlignmentPoint = Point(f, 13, mm);
                            }
                            else
                            {
                                double? at = Num(Field(f, 11));
                                e.TextAttachment = at.HasValue ? (int?)(int)Math.Round(at.Value) : null;
                                e.TextBoxWidthMm = Scale(Num(Field(f, 12)), mm);
                                e.TextBoxHeightMm = Scale(Num(Field(f, 13)), mm);
                                // An MTEXT's direction is its 11 vector when it has one (world, not OCS).
                                CadPoint? dir = Point(f, 14, 1.0);
                                if (dir.HasValue && (Math.Abs(dir.Value.X) > 1e-12 || Math.Abs(dir.Value.Y) > 1e-12))
                                    e.RotationRadians = Math.Atan2(dir.Value.Y, dir.Value.X);
                            }
                        }
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
                        // THE DYNAMIC BLOCK BEHIND AN ANONYMOUS REFERENCE (extractor fields 13-15;
                        // absent from dumps made before them, which is not "not dynamic").
                        string effective = Unescape(Field(f, 13));
                        if (!string.IsNullOrWhiteSpace(effective))
                        {
                            e.EffectiveName = effective;
                            e.EffectiveNameSource = Field(f, 14);
                            e.DynamicProperties = DynamicProperties(Field(f, 15));
                            string sig;
                            if (e.BlockName != null && definitionSignature.TryGetValue(e.BlockName, out sig))
                                e.DefinitionSignature = sig;
                        }
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
                        Appearance(e, f, 11, appearance);
                        break;

                    case "CIRCLE":
                        e.Kind = CadEntityKind.Circle;
                        AddPoint(e, Point(f, 5, mm));
                        e.RadiusMm = Scale(Num(Field(f, 8)), mm);
                        break;

                    case "ARC":
                    {
                        e.Kind = CadEntityKind.Arc;
                        Appearance(e, f, 11, appearance);
                        CadPoint? c = Point(f, 5, mm);
                        double? rad = Scale(Num(Field(f, 8)), mm);
                        double? a1 = Num(Field(f, 9)), a2 = Num(Field(f, 10));
                        AddPoint(e, c);
                        e.RadiusMm = rad;
                        if (c.HasValue && rad.HasValue && a1.HasValue && a2.HasValue)
                        {
                            // RADIANS, as entget hands them to the extractor (a DXF FILE stores
                            // degrees; the entity list does not). Read as degrees, a real
                            // extraction's 3.141593..4.712389 was an arc of 1.57 degrees
                            // (audit 2026-10-08, lote 7).
                            double r1 = a1.Value, r2 = a2.Value;
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

                    case "LEADER":
                    {
                        // A leader's vertices, arrowhead FIRST: which run a size label names is
                        // often said by the leader and not by distance.
                        e.Kind = CadEntityKind.Leader;
                        string lpts = Field(f, 5);
                        if (!string.IsNullOrWhiteSpace(lpts))
                            foreach (string v in lpts.Split(';'))
                            {
                                string[] xy = v.Split(',');
                                if (xy.Length < 2) continue;
                                double? x = Num(xy[0]), y = Num(xy[1]);
                                if (x.HasValue && y.HasValue) e.Points.Add(new CadPoint(x.Value * mm, y.Value * mm, 0));
                            }
                        break;
                    }

                    case "LWPOLYLINE":
                    {
                        e.Kind = CadEntityKind.Polyline;
                        Appearance(e, f, 8, appearance);
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

                    case "HATCH":
                    {
                        // Still unmodelled - the fill is not geometry anything builds -
                        // but its boundary is kept as evidence of solid material.
                        e.Kind = CadEntityKind.Unclassified;
                        e.UnmodelledType = type;
                        e.HatchPattern = Unescape(Field(f, 5));
                        bool partial;
                        e.HatchLoops = CadHatchLoops.Parse(Field(f, 8), mm, out partial);
                        e.HatchLoopsPartial = partial;
                        break;
                    }

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
                double[] extrusion;
                if (e.Handle != null && normals.TryGetValue(e.Handle, out extrusion) &&
                    !CadOcs.IsWorld(extrusion[0], extrusion[1], extrusion[2]))
                    ToWorld(e, type, extrusion, r);
                r.Entities.Add(e);
            }

            ResolveAppearance(r, appearance);
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

        /// <summary>"name=value;name=value" as the extractor writes an instance's dynamic
        /// properties; empty when none were set on the instance (the definition's defaults hold).</summary>
        public static Dictionary<string, string> DynamicProperties(string field)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(field)) return d;
            foreach (string pair in field.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                d[Unescape(pair.Substring(0, eq))] = Unescape(pair.Substring(eq + 1));
            }
            return d;
        }

        /// <summary>Per block definition: SHA-256 (24 hex) of its entity rows without handles and
        /// owners, in the order the definition lists them.</summary>
        public static Dictionary<string, string> DefinitionSignatures(IEnumerable<string[]> rows)
        {
            var text = new Dictionary<string, System.Text.StringBuilder>(StringComparer.Ordinal);
            foreach (string[] f in rows)
            {
                if (f.Length < 5 || f[0] != "E") continue;
                string owner = f[1];
                if (owner.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) ||
                    owner.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
                System.Text.StringBuilder sb;
                if (!text.TryGetValue(owner, out sb)) text[owner] = sb = new System.Text.StringBuilder();
                sb.Append(f[3]).Append('|').Append(f[4]);
                for (int i = 5; i < f.Length; i++) sb.Append('|').Append(f[i]);
                sb.Append('\n');
            }
            var outp = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                foreach (var kv in text)
                {
                    byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(kv.Value.ToString()));
                    outp[kv.Key] = BitConverter.ToString(h).Replace("-", "").ToLowerInvariant().Substring(0, 24);
                }
            return outp;
        }

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
