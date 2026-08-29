// -----------------------------------------------------------------------------
// Horizun Revit MCP - one read surface for structure, instead of a tool per noun.
// Original Horizun code. Read-only: this command opens no transaction.
//
// Steel members, reinforcement hosts, cover, bars, reinforcement systems and
// connections are one subject. Splitting them into eight tools would publish
// eight schemas that share a document, a pagination scheme and a coverage
// vocabulary, and would still not answer the question people actually ask -
// "what reinforcement does this beam carry, and did you see all of it".
//
// THE RULE THAT SHAPES EVERY REPLY. A count of zero and a count that could not
// be taken are different answers, and only one of them is a fact about the
// building. Every mode returns a coverage word beside its numbers and only
// `complete` means the number is a total. Nothing here ever reports zero for
// something it did not look at.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class QueryStructureCommand : ICommand
    {
        public string Name => "horizun_query_structure";
        public string Description =>
            "Read structural members, reinforcement hosts, cover, rebar, reinforcement systems and steel " +
            "connections, with measured geometry and explicit coverage. Read-only.";

        public const double FtToMm = 304.8;
        private const int DefaultRows = 50;
        private const int MaxRows = 500;

        public static readonly string[] Modes =
        {
            "members", "hosts", "covers", "rebar", "reinforcement_systems", "connections", "coverage",
            "quantities"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string mode = (request.Value<string>("mode") ?? "").Trim().ToLowerInvariant();
            if (!Modes.Contains(mode))
                return CommandResult.Fail("mode must be one of " + string.Join(", ", Modes) + ".");

            int maxRows = request.Value<int?>("max_rows") ?? DefaultRows;
            if (maxRows < 1 || maxRows > MaxRows)
                return CommandResult.Fail("max_rows must be between 1 and " + MaxRows + ".");
            int offset = request.Value<int?>("offset") ?? 0;
            if (offset < 0) return CommandResult.Fail("offset must not be negative.");

            var ids = new List<long>();
            foreach (JToken t in request["element_ids"] as JArray ?? new JArray())
            {
                long v = t.Value<long?>() ?? -1;
                if (!Rid.CanRepresent(v)) return CommandResult.Fail("element_ids carries a value that is not an ElementId: " + t);
                ids.Add(v);
            }

            try
            {
                switch (mode)
                {
                    case "members": return Members(doc, request, ids, offset, maxRows);
                    case "hosts": return Hosts(doc, request, ids, offset, maxRows);
                    case "covers": return Covers(doc, ids, offset, maxRows);
                    case "rebar": return Bars(doc, request, ids, offset, maxRows);
                    case "reinforcement_systems": return Systems(doc, ids, offset, maxRows);
                    case "connections": return Connections(doc, ids, offset, maxRows);
                    case "coverage": return Capability(doc);
                    case "quantities": return Quantities(doc, request, ids);
                    default: return CommandResult.Fail("unhandled mode " + mode + ".");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("The structural query failed: " + ex.Message +
                                          " Nothing was changed; this command opens no transaction.");
            }
        }

        // ------------------------------------------------------------- members

        private CommandResult Members(Document doc, JObject request, List<long> ids, int offset, int maxRows)
        {
            var wanted = new List<BuiltInCategory>();
            List<string> asked = Strings(request["categories"] as JArray);
            if (asked.Count == 0)
            {
                wanted.Add(BuiltInCategory.OST_StructuralFraming);
                wanted.Add(BuiltInCategory.OST_StructuralColumns);
            }
            else
            {
                foreach (string s in asked)
                {
                    BuiltInCategory bic;
                    if (!Enum.TryParse(s, out bic))
                        return CommandResult.Fail("categories carries '" + s + "', which is not a BuiltInCategory name.");
                    wanted.Add(bic);
                }
            }

            List<Element> all = Collect(doc, wanted, ids);
            var reasons = new JArray();
            var rows = new JArray();
            foreach (Element e in all.Skip(offset).Take(maxRows))
                rows.Add(Member(doc, e, reasons));

            return Ok("members", all.Count, offset, rows, reasons,
                      new JObject { ["categories"] = new JArray(wanted.Select(c => c.ToString()).ToArray()) });
        }

        private static JObject Member(Document doc, Element e, JArray reasons)
        {
            var o = new JObject
            {
                ["id"] = Rid.Value(e.Id),
                ["unique_id"] = Str(() => e.UniqueId),
                ["name"] = Str(() => e.Name),
                ["category"] = e.Category == null ? null : e.Category.Name
            };
            var fi = e as FamilyInstance;
            ElementType type = doc.GetElement(e.GetTypeId()) as ElementType;
            o["family"] = type == null ? null : Str(() => type.FamilyName);
            o["type"] = type == null ? null : Str(() => type.Name);
            o["type_id"] = type == null ? -1 : Rid.Value(type.Id);

            if (fi == null)
            {
                o["structural_type"] = null;
                o["is_family_instance"] = false;
                reasons.Add(StructuralCoverage.Reason("structural_type",
                    "this element is not a family instance, so it carries no StructuralType.", Rid.Value(e.Id)));
            }
            else
            {
                o["is_family_instance"] = true;
                o["structural_type"] = Str(() => fi.StructuralType.ToString());
                o["structural_usage"] = Str(() => fi.StructuralUsage.ToString());
                o["mirrored"] = Bool(() => fi.Mirrored);
                o["hand_flipped"] = Bool(() => fi.HandFlipped);
                o["facing_flipped"] = Bool(() => fi.FacingFlipped);
            }

            // THE LOCATION LINE AND THE SECTION ROTATION ARE DIFFERENT THINGS, and
            // conflating them is the classic structural modelling error: a beam can
            // run north and have its web turned 90 degrees, and only one of those
            // two numbers says so.
            var loc = new JObject();
            var lc = e.Location as LocationCurve;
            var lp = e.Location as LocationPoint;
            if (lc != null && lc.Curve != null)
            {
                Curve c = lc.Curve;
                loc["kind"] = "curve";
                loc["is_line"] = c is Line;
                loc["start_mm"] = RebarFacts.Xyz(Pt(c, 0), 3, FtToMm);
                loc["end_mm"] = RebarFacts.Xyz(Pt(c, 1), 3, FtToMm);
                loc["length_mm"] = Round(Num(() => c.Length) * FtToMm);
                loc["direction"] = (c is Line) ? RebarFacts.Xyz(((Line)c).Direction, 6) : null;
            }
            else if (lp != null)
            {
                loc["kind"] = "point";
                loc["point_mm"] = RebarFacts.Xyz(lp.Point, 3, FtToMm);
                loc["rotation_radians"] = Round(Num(() => lp.Rotation), 6);
                loc["rotation_means"] = "the PLAN rotation of a point-hosted member. Not the cross-section rotation.";
            }
            else
            {
                loc["kind"] = "unreadable";
                reasons.Add(StructuralCoverage.Reason("location",
                    "the element carries neither a location curve nor a location point.", Rid.Value(e.Id)));
            }
            o["location"] = loc;

            o["cross_section_rotation_radians"] =
                Round(ParamNumber(e, BuiltInParameter.STRUCTURAL_BEND_DIR_ANGLE), 6);
            o["cross_section_rotation_means"] =
                "the section turned about the member axis. A beam that runs north with its web rotated 90 " +
                "degrees has an unchanged location line and this number changed.";

            o["level"] = LevelBlock(doc, e);
            o["start_level_offset_mm"] = Round(ParamNumber(e, BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION) * FtToMm);
            o["end_level_offset_mm"] = Round(ParamNumber(e, BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION) * FtToMm);
            o["justification"] = Str(() => ParamString(e, BuiltInParameter.YZ_JUSTIFICATION));

            // Joins: measured through the API, not read off a parameter.
            if (fi != null && e.Category != null &&
                Rid.Value(e.Category.Id) == (long)BuiltInCategory.OST_StructuralFraming)
            {
                var joins = new JArray();
                for (int end = 0; end <= 1; end++)
                {
                    bool? allowed = null;
                    try { allowed = StructuralFramingUtils.IsJoinAllowedAtEnd(fi, end); } catch { }
                    joins.Add(new JObject { ["end"] = end, ["join_allowed"] = allowed });
                    if (allowed == null)
                        reasons.Add(StructuralCoverage.Reason("join_state",
                            "Revit would not report whether a join is allowed at end " + end + ".", Rid.Value(e.Id)));
                }
                o["joins"] = joins;
            }
            else
            {
                // `null` said nothing about why. A structural column has no joinable
                // ends in the sense StructuralFramingUtils means, and that is a fact
                // about the category rather than a read that failed.
                o["joins"] = null;
                o["joins_why"] = fi == null
                    ? "this element is not a family instance, so it has no framing ends."
                    : "StructuralFramingUtils answers about structural FRAMING; this element is in " +
                      (e.Category == null ? "no category" : e.Category.Name) + ".";
            }

            o["material"] = MaterialBlock(doc, e);
            o["volume_m3"] = Round(ParamNumber(e, BuiltInParameter.HOST_VOLUME_COMPUTED).HasValue
                ? (double?)Guard.ToM3(ParamNumber(e, BuiltInParameter.HOST_VOLUME_COMPUTED).Value) : null, 6);

            // Can this member carry reinforcement at all? Asked here so a caller
            // does not have to run a second mode to find out.
            bool? reinforceable = null;
            try { reinforceable = RebarHostData.IsValidHost(e); } catch { }
            o["is_rebar_host"] = reinforceable;
            return o;
        }

        // --------------------------------------------------------------- hosts

        private CommandResult Hosts(Document doc, JObject request, List<long> ids, int offset, int maxRows)
        {
            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFoundation
            };
            List<Element> all = Collect(doc, cats, ids);

            var reasons = new JArray();
            var rows = new JArray();
            int eligible = 0;
            foreach (Element e in all)
            {
                bool valid = false;
                try { valid = RebarHostData.IsValidHost(e); } catch { }
                if (valid) eligible++;
            }
            foreach (Element e in all.Skip(offset).Take(maxRows))
                rows.Add(Host(doc, e, reasons));

            JObject extra = new JObject
            {
                ["candidates_examined"] = all.Count,
                ["eligible_hosts"] = eligible,
                ["eligible_means"] =
                    "RebarHostData.IsValidHost said yes. An ineligible element is not a failure - a curtain " +
                    "wall and an in-place mass simply cannot carry rebar - but nothing may be planned onto it."
            };
            return Ok("hosts", all.Count, offset, rows, reasons, extra);
        }

        private static JObject Host(Document doc, Element e, JArray reasons)
        {
            var o = new JObject
            {
                ["id"] = Rid.Value(e.Id),
                ["name"] = Str(() => e.Name),
                ["category"] = e.Category == null ? null : e.Category.Name
            };
            bool valid = false;
            try { valid = RebarHostData.IsValidHost(e); } catch { }
            o["is_valid_host"] = valid;
            // THE BOX A PLAN IS MEASURED AGAINST. Published because a caller writing
            // a reinforcement rule has to put the bar somewhere inside this host,
            // and asking them to guess its extent - then refusing the result for
            // not fitting - is a round trip nobody needs.
            BoundingBoxXYZ box = null;
            try { box = e.get_BoundingBox(null); } catch { }
            o["bounding_box_mm"] = box == null ? (JToken)JValue.CreateNull() : new JObject
            {
                ["min"] = RebarFacts.Xyz(box.Min, 3, FtToMm),
                ["max"] = RebarFacts.Xyz(box.Max, 3, FtToMm),
                ["means"] = "the axis-aligned extent Revit reports for the whole element, in model coordinates. " +
                            "It is a BOX: an L-shaped slab's box covers the courtyard it does not have."
            };
            if (!valid)
            {
                o["cover"] = null;
                o["reinforcement"] = null;
                o["coverage"] = StructuralCoverage.Declare(StructuralCoverage.NotApplicable, 0, 0,
                    new JArray(StructuralCoverage.Reason("host",
                        "Revit does not accept this element as a reinforcement host, so cover and bars do not " +
                        "arise for it.", Rid.Value(e.Id))));
                return o;
            }

            RebarHostData data = null;
            try { data = RebarHostData.GetRebarHostData(e); } catch { }
            if (data == null)
            {
                o["cover"] = null;
                o["reinforcement"] = null;
                reasons.Add(StructuralCoverage.Reason("rebar_host_data",
                    "the element is a valid host and Revit would not return its host data.", Rid.Value(e.Id)));
                o["coverage"] = StructuralCoverage.Declare(StructuralCoverage.Unreadable, 0, 1);
                return o;
            }

            using (data)
            {
                var local = new JArray();
                var cover = new JObject();
                RebarCoverType common = null;
                try { common = data.GetCommonCoverType(); } catch { }
                cover["common"] = CoverBlock(common);
                cover["common_means"] =
                    "the one cover type that applies to every face, or null when the faces differ - null here " +
                    "is a fact about the host, not a failure to read it.";

                // PER FACE, because a slab with one cover on top and another
                // underneath reports null for the common type, and a report that
                // stopped there would say the host has no cover at all.
                var faces = new JArray();
                IList<Reference> exposed = null;
                try { exposed = data.GetExposedFaces(); } catch { }
                if (exposed == null)
                {
                    cover["faces"] = null;
                    local.Add(StructuralCoverage.Reason("exposed_faces",
                        "Revit would not list the faces of this host.", Rid.Value(e.Id)));
                }
                else
                {
                    foreach (Reference r in exposed)
                    {
                        RebarCoverType ct = null;
                        try { ct = data.GetCoverType(r); } catch { }
                        var row = new JObject
                        {
                            ["face"] = Str(() => r.ConvertToStableRepresentation(doc)),
                            ["cover"] = CoverBlock(ct)
                        };
                        if (ct == null)
                            local.Add(StructuralCoverage.Reason("face_cover",
                                "a face of this host carries no readable cover type.", Rid.Value(e.Id)));
                        faces.Add(row);
                    }
                    cover["faces"] = faces;
                    cover["face_count"] = exposed.Count;
                }
                o["cover"] = cover;

                var reinf = new JObject();
                reinf["rebar_ids"] = Ids(() => data.GetRebarsInHost().Select(b => (Element)b).ToList());
                reinf["area_reinforcement_ids"] = Ids(() => data.GetAreaReinforcementsInHost().Select(b => (Element)b).ToList());
                reinf["path_reinforcement_ids"] = Ids(() => data.GetPathReinforcementsInHost().Select(b => (Element)b).ToList());
                reinf["fabric_area_ids"] = Ids(() => data.GetFabricAreasInHost().Select(b => (Element)b).ToList());
                reinf["fabric_sheet_ids"] = Ids(() => data.GetFabricSheetsInHost().Select(b => (Element)b).ToList());
                o["reinforcement"] = reinf;

                foreach (JToken t in local) reasons.Add(t);
                o["coverage"] = StructuralCoverage.Declare(
                    local.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                    1, local.Count == 0 ? 0 : 1, local);
            }
            return o;
        }

        private static JToken CoverBlock(RebarCoverType t)
        {
            if (t == null) return JValue.CreateNull();
            return new JObject
            {
                ["id"] = Rid.Value(t.Id),
                ["name"] = Str(() => t.Name),
                // MEASURED, not read off a localised parameter name. The distance is
                // the fact; the name is whatever somebody typed.
                ["distance_mm"] = Round(Num(() => t.CoverDistance) * FtToMm)
            };
        }

        // -------------------------------------------------------------- covers

        private CommandResult Covers(Document doc, List<long> ids, int offset, int maxRows)
        {
            // max_rows, offset and element_ids are in this command's published schema
            // for every mode, and this one ignored all three - returning the whole
            // list and reporting offset 0 whatever was sent.
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(RebarCoverType))
                .Cast<RebarCoverType>()
                .OrderBy(t => Num(() => t.CoverDistance) ?? double.MaxValue)
                .ThenBy(t => Rid.Value(t.Id))
                .ToList();
            if (ids != null && ids.Count > 0)
                all = all.Where(t => ids.Contains(Rid.Value(t.Id))).ToList();

            var rows = new JArray();
            foreach (RebarCoverType t in all.Skip(offset).Take(maxRows)) rows.Add(CoverBlock(t));
            return Ok("covers", all.Count, offset, rows, new JArray(), new JObject
            {
                ["means"] = "every cover type defined in this document, ordered by distance. A cover type is a " +
                            "definition, not an assignment: use mode=hosts to see which face carries which."
            });
        }

        // --------------------------------------------------------------- rebar

        /// <summary>
        /// The same bars, grouped the way somebody orders steel: by mark, by
        /// diameter, by host, by rule - and for a stirrup zone rule the rule id is
        /// parent#zone, so grouping by rule groups BY ZONE.
        ///
        /// This is a mode rather than a tool because it reads exactly what
        /// mode=rebar reads and then adds up columns of it. A takeoff that could
        /// drift from the reader it summarises would be worse than no takeoff.
        ///
        /// It reads EVERY bar in scope, not a page of them: a total over the first
        /// fifty is not a total. That is why there is no max_rows here.
        /// </summary>
        private CommandResult Quantities(Document doc, JObject request, List<long> ids)
        {
            var groupBy = new List<string>();
            foreach (JToken t in request["group_by"] as JArray ?? new JArray())
            {
                string k = (t == null ? null : t.Value<string>());
                if (!string.IsNullOrWhiteSpace(k)) groupBy.Add(k.Trim());
            }
            if (groupBy.Count == 0) groupBy.Add(RebarTakeoffKey.Mark);

            double? density = request.Value<double?>("density_kg_per_m3");
            string densitySource = request.Value<string>("density_source");

            long hostFilter = request.Value<long?>("host_id") ?? -1;
            List<Rebar> all;
            if (ids.Count > 0)
            {
                all = new List<Rebar>();
                foreach (long id in ids)
                {
                    var b = doc.GetElement(Rid.Make(id)) as Rebar;
                    if (b != null) all.Add(b);
                }
            }
            else
            {
                all = new FilteredElementCollector(doc).OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
            }
            if (Rid.CanRepresent(hostFilter) && hostFilter >= 0)
            {
                ElementId want = Rid.Make(hostFilter);
                all = all.Where(b =>
                {
                    try { return b.GetHostId() == want; } catch { return false; }
                }).ToList();
            }
            all = all.OrderBy(b => Rid.Value(b.Id)).ToList();

            var rows = new JArray();
            var reasons = new JArray();
            foreach (Rebar b in all)
            {
                // Positions are not needed for a takeoff and are the expensive part
                // of describing a set - Revit computes a transform per bar.
                JObject row = RebarFacts.Describe(doc, b, false);
                rows.Add(row);
                string word = (string)(row["coverage"] == null ? null : row["coverage"]["coverage"]);
                if (word != null && word != StructuralCoverage.Complete)
                    reasons.Add(StructuralCoverage.Reason("rebar",
                        "set " + Rid.Value(b.Id) + " could not be described completely, so it may be missing " +
                        "from a total.", Rid.Value(b.Id)));
            }

            string error;
            JObject takeoff = RebarTakeoff.Group(rows, groupBy, density, densitySource, out error);
            if (takeoff == null) return CommandResult.Fail(error);

            takeoff["scope"] = new JObject
            {
                ["bar_sets_in_scope"] = all.Count,
                ["element_ids_given"] = ids.Count,
                ["host_id"] = Rid.CanRepresent(hostFilter) && hostFilter >= 0 ? (JToken)hostFilter : JValue.CreateNull(),
                ["means"] = "every rebar set in scope was read, not a page of them: a total over the first " +
                            "fifty rows is not a total, so this mode does not paginate."
            };
            takeoff["coverage"] = StructuralCoverage.Declare(
                reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                all.Count, reasons.Count, reasons);
            return CommandResult.Ok(takeoff);
        }

        private CommandResult Bars(Document doc, JObject request, List<long> ids, int offset, int maxRows)
        {
            bool positions = request["include_bar_positions"] == null || request.Value<bool>("include_bar_positions");
            long hostFilter = request.Value<long?>("host_id") ?? -1;

            List<Rebar> all;
            if (ids.Count > 0)
            {
                all = new List<Rebar>();
                foreach (long id in ids)
                {
                    var b = doc.GetElement(Rid.Make(id)) as Rebar;
                    if (b != null) all.Add(b);
                }
            }
            else
            {
                all = new FilteredElementCollector(doc)
                    .OfClass(typeof(Rebar)).Cast<Rebar>().ToList();
            }
            if (Rid.CanRepresent(hostFilter) && hostFilter >= 0)
            {
                ElementId want = Rid.Make(hostFilter);
                all = all.Where(b =>
                {
                    try { return b.GetHostId() == want; } catch { return false; }
                }).ToList();
            }
            all = all.OrderBy(b => Rid.Value(b.Id)).ToList();

            var reasons = new JArray();
            var rows = new JArray();
            foreach (Rebar b in all.Skip(offset).Take(maxRows))
            {
                JObject row = RebarFacts.Describe(doc, b, positions);
                JArray rr = row["coverage"]?["reasons"] as JArray;
                if (rr != null) foreach (JToken t in rr) reasons.Add(t);
                rows.Add(row);
            }

            // MEASURED TOTALS over the page, and named as such. A total over a page
            // is not a total over a model, and calling it one is how a takeoff goes
            // out short.
            double lengthMm = 0; int bars = 0; bool everyLength = true;
            foreach (JToken r in rows)
            {
                JToken l = r["measured"]?["total_length_mm"];
                if (l == null || l.Type == JTokenType.Null) everyLength = false;
                else lengthMm += (double)l;
                int? q = r["measured"]?["quantity"]?.Value<int?>();
                if (q.HasValue) bars += q.Value; else everyLength = false;
            }

            JObject extra = new JObject
            {
                ["page_total_bars"] = bars,
                ["page_total_length_mm"] = everyLength ? (JToken)Math.Round(lengthMm, 3) : JValue.CreateNull(),
                ["page_totals_mean"] =
                    "summed over the ROWS RETURNED, not over the model. Page through to the end, or use " +
                    "mode=rebar with a host_id, before treating either number as a takeoff.",
                ["host_id_filter"] = hostFilter >= 0 ? (JToken)hostFilter : JValue.CreateNull()
            };
            return Ok("rebar", all.Count, offset, rows, reasons, extra);
        }

        // -------------------------------------------------- reinforcement systems

        private CommandResult Systems(Document doc, List<long> ids, int offset, int maxRows)
        {
            var rows = new JArray();
            var reasons = new JArray();
            var items = new List<Element>();
            items.AddRange(new FilteredElementCollector(doc).OfClass(typeof(AreaReinforcement)).ToList());
            items.AddRange(new FilteredElementCollector(doc).OfClass(typeof(PathReinforcement)).ToList());
            items.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FabricArea)).ToList());
            items.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FabricSheet)).ToList());
            if (ids.Count > 0) items = items.Where(e => ids.Contains(Rid.Value(e.Id))).ToList();
            items = items.OrderBy(e => Rid.Value(e.Id)).ToList();

            foreach (Element e in items.Skip(offset).Take(maxRows))
            {
                var o = new JObject
                {
                    ["id"] = Rid.Value(e.Id),
                    ["name"] = Str(() => e.Name),
                    ["kind"] = e.GetType().Name,
                    ["category"] = e.Category == null ? null : e.Category.Name
                };
                var ar = e as AreaReinforcement;
                var pr = e as PathReinforcement;
                if (ar != null)
                {
                    o["host_id"] = Rid.Value(Safe(() => ar.GetHostId()));
                    o["major_direction"] = RebarFacts.Xyz(SafeXyz(() => ar.Direction), 6);
                    o["member_rebar_ids"] = IdArray(() => ar.GetRebarInSystemIds());
                    o["boundary_curve_ids"] = IdArray(() => ar.GetBoundaryCurveIds());
                    var layers = new JArray();
                    foreach (AreaReinforcementLayerType layer in Enum.GetValues(typeof(AreaReinforcementLayerType)))
                    {
                        bool? active = null; int? lines = null;
                        try { active = ar.IsLayerActive(layer); } catch { }
                        try { lines = ar.GetNumberOfLines(layer); } catch { }
                        layers.Add(new JObject
                        {
                            ["layer"] = layer.ToString(),
                            ["active"] = active,
                            ["lines"] = lines,
                            ["direction"] = RebarFacts.Xyz(SafeXyz(() => ar.GetLayerDirection(layer)), 6)
                        });
                    }
                    o["layers"] = layers;
                }
                else if (pr != null)
                {
                    o["host_id"] = Rid.Value(Safe(() => pr.GetHostId()));
                    o["member_rebar_ids"] = IdArray(() => pr.GetRebarInSystemIds());
                    o["path_curve_ids"] = IdArray(() => pr.GetCurveElementIds());
                    o["primary_bar_orientation"] = Str(() => pr.PrimaryBarOrientation.ToString());
                    o["alternating_enabled"] = Bool(() => pr.IsAlternatingLayerEnabled());
                }
                else
                {
                    // Fabric is listed and NOT described in detail: this bridge does
                    // not yet read it, and a row that pretended otherwise would be a
                    // claim. See docs/STRUCTURAL-PROGRAM.md.
                    o["detail"] = null;
                    reasons.Add(StructuralCoverage.Reason("fabric",
                        "fabric reinforcement is listed but not described: this bridge does not read " +
                        "FabricArea or FabricSheet beyond their identity.", Rid.Value(e.Id)));
                }
                rows.Add(o);
            }
            return Ok("reinforcement_systems", items.Count, offset, rows, reasons, null);
        }

        // --------------------------------------------------------- connections

        private CommandResult Connections(Document doc, List<long> ids, int offset, int maxRows)
        {
            var items = new FilteredElementCollector(doc)
                .OfClass(typeof(StructuralConnectionHandler))
                .Cast<StructuralConnectionHandler>()
                .OrderBy(c => Rid.Value(c.Id)).ToList();
            if (ids.Count > 0) items = items.Where(e => ids.Contains(Rid.Value(e.Id))).ToList();

            var reasons = new JArray();
            var rows = new JArray();
            foreach (StructuralConnectionHandler c in items.Skip(offset).Take(maxRows))
            {
                ElementType t = doc.GetElement(c.GetTypeId()) as ElementType;
                rows.Add(new JObject
                {
                    ["id"] = Rid.Value(c.Id),
                    ["type_id"] = t == null ? -1 : Rid.Value(t.Id),
                    ["type"] = t == null ? null : Str(() => t.Name),
                    ["connected_element_ids"] = IdArray(() => c.GetConnectedElementIds()),
                    ["origin_mm"] = RebarFacts.Xyz(SafeXyz(() => c.GetOrigin()), 3, FtToMm),
                    ["is_custom"] = Bool(() => c.IsCustom()),
                    ["is_detailed"] = Bool(() => c.IsDetailed()),
                    ["is_detailed_means"] =
                        "a detailed connection carries fabrication geometry - plates, bolts, welds. A generic " +
                        "one is a placeholder that records WHICH members meet and nothing about how."
                });
            }
            return Ok("connections", items.Count, offset, rows, reasons, null);
        }

        // ------------------------------------------------------------ coverage

        /// <summary>
        /// What this bridge can and cannot answer about structure IN THIS REVIT.
        /// Published so a caller never has to infer a capability from an empty
        /// result, which is the one inference that is always wrong.
        /// </summary>
        private CommandResult Capability(Document doc)
        {
            var unreadable = new List<string>();
            var o = new JObject
            {
                ["mode"] = "coverage",
                ["revit_year"] = doc.Application.VersionNumber,
                ["rebar_api_generation"] = RebarApi.ApiGeneration,
                ["orientation_vocabulary"] = new JArray(RebarApi.Orientations),
                ["layout_vocabulary"] = new JArray(RebarLayout.All),
                ["coverage_vocabulary"] = new JArray(StructuralCoverage.All),
                ["reinforcement_enabled"] = Bool(() =>
                    ReinforcementSettings.GetReinforcementSettings(doc).HostStructuralRebar),
                ["reinforcement_enabled_means"] =
                    "Revit's own host-structural-rebar setting. When it is false, bars can still be created but " +
                    "are not hosted the way a schedule expects, and this is the first thing to check when a " +
                    "plan resolves a host and the model refuses it.",
                ["counts"] = new JObject
                {
                    ["rebar"] = Count(doc, typeof(Rebar), unreadable),
                    ["rebar_bar_types"] = Count(doc, typeof(RebarBarType), unreadable),
                    ["rebar_shapes"] = Count(doc, typeof(RebarShape), unreadable),
                    ["rebar_hook_types"] = Count(doc, typeof(RebarHookType), unreadable),
                    ["rebar_cover_types"] = Count(doc, typeof(RebarCoverType), unreadable),
                    ["area_reinforcement"] = Count(doc, typeof(AreaReinforcement), unreadable),
                    ["path_reinforcement"] = Count(doc, typeof(PathReinforcement), unreadable),
                    ["fabric_areas"] = Count(doc, typeof(FabricArea), unreadable),
                    ["fabric_sheets"] = Count(doc, typeof(FabricSheet), unreadable),
                    ["structural_connections"] = Count(doc, typeof(StructuralConnectionHandler), unreadable),
                    ["couplers"] = Count(doc, typeof(RebarCoupler), unreadable)
                },
                ["not_read_by_this_bridge"] = new JArray(
                    "FabricArea and FabricSheet beyond identity",
                    "analytical members and panels",
                    "steel fabrication geometry inside a detailed connection - plates, bolts, welds")
            };
            // A COVERAGE WORD, like every other mode. This one published bare counts
            // and nothing saying whether they were all taken.
            var reasons = new JArray();
            foreach (string cls in unreadable)
                reasons.Add(StructuralCoverage.Reason("count",
                    "the collector for " + cls + " would not answer, so its count is null rather than zero."));
            o["coverage"] = StructuralCoverage.Declare(
                unreadable.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial,
                11 - unreadable.Count, unreadable.Count, reasons);
            return CommandResult.Ok(o);
        }

        // ------------------------------------------------------------- helpers

        private CommandResult Ok(string mode, int matched, int offset, JArray rows, JArray reasons, JObject extra)
        {
            int returned = rows.Count;
            int next = offset + returned;
            string coverage = reasons.Count == 0 ? StructuralCoverage.Complete : StructuralCoverage.Partial;
            var o = new JObject
            {
                ["mode"] = mode,
                ["matched"] = matched,
                ["returned"] = returned,
                ["offset"] = offset,
                ["next_offset"] = next < matched ? (JToken)next : JValue.CreateNull(),
                ["complete_page"] = next >= matched,
                ["rows"] = rows
            };
            if (extra != null) foreach (JProperty p in extra.Properties()) o[p.Name] = p.Value;
            o["coverage"] = StructuralCoverage.Declare(coverage, returned, reasons.Count, reasons);
            return CommandResult.Ok(o);
        }

        private static List<Element> Collect(Document doc, IList<BuiltInCategory> cats, List<long> ids)
        {
            if (ids != null && ids.Count > 0)
            {
                var picked = new List<Element>();
                foreach (long id in ids)
                {
                    Element e = doc.GetElement(Rid.Make(id));
                    if (e != null) picked.Add(e);
                }
                return picked.OrderBy(e => Rid.Value(e.Id)).ToList();
            }
            var filter = new ElementMulticategoryFilter(cats.ToList());
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .OrderBy(e => Rid.Value(e.Id))
                .ToList();
        }

        private static JObject LevelBlock(Document doc, Element e)
        {
            ElementId id = null;
            try { id = e.LevelId; } catch { }
            var level = (id != null && id != ElementId.InvalidElementId) ? doc.GetElement(id) as Level : null;
            if (level == null) return null;
            return new JObject
            {
                ["id"] = Rid.Value(level.Id),
                ["name"] = Str(() => level.Name),
                ["elevation_mm"] = Round(Num(() => level.Elevation) * FtToMm)
            };
        }

        private static JToken MaterialBlock(Document doc, Element e)
        {
            try
            {
                ICollection<ElementId> mats = e.GetMaterialIds(false);
                if (mats == null || mats.Count == 0) return JValue.CreateNull();
                var a = new JArray();
                foreach (ElementId m in mats)
                {
                    var mat = doc.GetElement(m) as Material;
                    if (mat == null) continue;
                    a.Add(new JObject { ["id"] = Rid.Value(mat.Id), ["name"] = Str(() => mat.Name) });
                }
                return a;
            }
            catch { return JValue.CreateNull(); }
        }

        /// <summary>
        /// A count, or null when the collector would not answer. It returned -1,
        /// which is a NUMBER: a caller adding these up got a total quietly reduced by
        /// one for every question that failed, in a command whose whole subject is
        /// that zero and unmeasured are different.
        /// </summary>
        private static JToken Count(Document doc, Type t, List<string> unreadable)
        {
            try { return new FilteredElementCollector(doc).OfClass(t).GetElementCount(); }
            catch
            {
                unreadable.Add(t.Name);
                return JValue.CreateNull();
            }
        }

        private static List<string> Strings(JArray a)
        {
            var list = new List<string>();
            if (a == null) return list;
            foreach (JToken t in a)
            {
                string s = t.Value<string>();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
            return list;
        }

        private static XYZ Pt(Curve c, int i)
        {
            try { return c.GetEndPoint(i); } catch { return null; }
        }

        private static double? ParamNumber(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return p.AsDouble();
            }
            catch { return null; }
        }

        private static string ParamString(Element e, BuiltInParameter bip)
        {
            try
            {
                Parameter p = e.get_Parameter(bip);
                if (p == null || !p.HasValue) return null;
                return p.AsValueString() ?? p.AsString();
            }
            catch { return null; }
        }

        private static JToken Ids(Func<List<Element>> f)
        {
            try
            {
                List<Element> list = f();
                if (list == null) return JValue.CreateNull();
                return new JArray(list.Where(x => x != null).Select(x => Rid.Value(x.Id)).Cast<object>().ToArray());
            }
            catch { return JValue.CreateNull(); }
        }

        private static JToken IdArray(Func<IList<ElementId>> f)
        {
            try
            {
                IList<ElementId> list = f();
                if (list == null) return JValue.CreateNull();
                return new JArray(list.Select(Rid.Value).Cast<object>().ToArray());
            }
            catch { return JValue.CreateNull(); }
        }

        private static ElementId Safe(Func<ElementId> f)
        {
            try { return f(); } catch { return null; }
        }

        private static XYZ SafeXyz(Func<XYZ> f)
        {
            try { return f(); } catch { return null; }
        }

        private static string Str(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private static double? Num(Func<double> f)
        {
            try { return f(); } catch { return null; }
        }

        private static bool? Bool(Func<bool> f)
        {
            try { return f(); } catch { return null; }
        }

        private static JToken Round(double? v, int digits = 3)
        {
            if (!v.HasValue) return JValue.CreateNull();
            return new JValue(Math.Round(v.Value, digits));
        }
    }
}
