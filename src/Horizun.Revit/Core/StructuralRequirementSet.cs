// -----------------------------------------------------------------------------
// Horizun Revit MCP - the neutral artefact that says what reinforcement is wanted.
// Original Horizun code. No Revit types.
//
// THE POINT OF THIS FILE IS WHAT IT REFUSES TO DECIDE.
//
// A bridge that models reinforcement is one small step from designing it, and
// that step is not ours to take. Diameter, spacing, cover, grade, hook angle,
// lap length, ratio: every one of those is a number somebody is answerable for.
// They arrive HERE, declared, from a person or from a code that person chose.
// Nothing in this bridge invents one, and nothing here carries a default that
// would quietly become one.
//
// So this parser is strict in a specific direction. A missing value is never
// filled in - it is a refusal that names the field. A value that this layout
// would not use is a refusal too, because somebody wrote it meaning something.
// The only defaults that exist are ones that cannot change a bar: whether an
// end bar is included, which is Revit's own default and is echoed back so it
// shows in the plan.
//
// A malformed set is refused ENTIRELY. Half a requirement set is worse than
// none: it builds some of somebody's reinforcement and leaves them believing
// they asked for what arrived.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class StructuralBarTypeRef
    {
        public string Id;
        public string TypeName;
        public double? NominalDiameterMm;
    }

    public sealed class StructuralHookTypeRef
    {
        public string Id;
        public string TypeName;
        /// <summary>Null means "no hook". A hook type of null and a missing hook block are the same thing.</summary>
        public bool None;
    }

    public sealed class StructuralHostSelector
    {
        public string Category;
        public string TypeName;
        public List<long> ElementIds = new List<long>();
        public bool Any { get { return Category != null || TypeName != null || ElementIds.Count > 0; } }
    }

    public sealed class StructuralCoverRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        /// <summary>common, or a named face. Only `common` is implemented; anything else is refused by name.</summary>
        public string Face;
        public string CoverTypeName;
        public double? DistanceMm;
        public bool Required = true;
    }

    public sealed class StructuralTermination
    {
        /// <summary>The id of a hook type declared in hook_types, or null for no hook.</summary>
        public string HookTypeId;
        public string Orientation = RebarApiVocabulary.OrientationLeft;
    }

    /// <summary>
    /// How a stirrup zone rule learns the cover Revit will clamp its array to.
    ///
    /// MEASURED (ADR-003 item 7): Revit shifts a hosted array in by the host's
    /// cover plus the bar's radius at each end, whatever the declaration says. A
    /// zone rule that does not know the cover declares stations Revit then moves,
    /// and the apply correctly reports a model that does not carry what was asked
    /// for. This block is how the zone planner is TOLD the cover - read from the
    /// host at resolve time, or declared - so that what it predicts is what Revit
    /// draws. Absent, the zones are laid out in model coordinates exactly as
    /// declared, which is the behaviour every existing set was written against.
    /// </summary>
    public sealed class StructuralStirrupZoneCover
    {
        public const string SourceHost = "host";
        public const string SourceDeclared = "declared";
        public static readonly string[] Sources = { SourceHost, SourceDeclared };

        public string Source;
        /// <summary>Required when Source is declared; refused beside host, because two statements of one number disagree.</summary>
        public double? DistanceMm;
    }

    /// <summary>
    /// What a mat does about the openings in its host. Every word here is a
    /// design decision, which is why none of them is defaulted: a mat rule over a
    /// host with a hole and no block is refused BY NAME as soon as a bar would
    /// cross the hole, naming the openings found and the three policies.
    /// </summary>
    public sealed class StructuralMatOpenings
    {
        public const string PolicyOmit = "omit";
        public const string PolicyTrim = "trim";
        public const string PolicyIgnore = "ignore";
        public static readonly string[] Policies = { PolicyOmit, PolicyTrim, PolicyIgnore };

        public string Policy;
        /// <summary>An opening whose largest dimension is below this is ignored. Declared, never defaulted.</summary>
        public double MinimumSizeMm;
        /// <summary>trim only: how far short of the opening a trimmed bar stops. Refused beside omit or ignore.</summary>
        public double? ClearanceMm;
    }

    /// <summary>
    /// Stirrups declared the way a drawing declares them: a profile, a direction,
    /// and zones along it. This is NOT a fourth kind of thing in the model - it
    /// expands into ordinary reinforcement rules, one per zone, so containment,
    /// the point-by-point audit, provenance and idempotency need to know nothing
    /// about it.
    /// </summary>
    public sealed class StructuralStirrupZoneRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public string BarTypeId;
        public string ShapeName;
        public string Style = StructuralStyle.StirrupTie;
        /// <summary>The stirrup outline at the START of the span, in model coordinates.</summary>
        public List<double[]> ProfileMm = new List<double[]>();
        public bool Closed = true;
        /// <summary>The direction the zones run in. Same role as a rebar rule's normal.</summary>
        public double[] AlongMm;
        /// <summary>How long the run is. Null means "measure the host", which only the resolver can do.</summary>
        public double? SpanMm;
        public bool SpanFromHost;
        public double StartOffsetMm;
        public double EndOffsetMm;
        public bool Symmetric;
        public double? MinimumClearBetweenZonesMm;
        /// <summary>Null means "no cover block": model coordinates as declared. See StructuralStirrupZoneCover.</summary>
        public StructuralStirrupZoneCover Cover;
        public List<StirrupZoneRequest> Zones = new List<StirrupZoneRequest>();
        public StructuralTermination Start = new StructuralTermination();
        public StructuralTermination End = new StructuralTermination();
        public string Mark;
        public bool Required = true;
        public bool AllowNewShape;
        public JObject Raw;
    }

    /// <summary>
    /// A slab or wall mat: "top X at 150, top Y at 200". Expands into ordinary
    /// reinforcement rules, one per component, with the centrelines derived from
    /// the host's own boundary rather than typed out by hand.
    /// </summary>
    public sealed class StructuralMatRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        /// <summary>Points OUT of the face the mat sits under. Declared, never inferred.</summary>
        public double[] FaceNormalMm;
        public List<MatComponentRequest> Components = new List<MatComponentRequest>();
        /// <summary>Null means "no openings block": a bar that would cross a hole refuses the rule by name.</summary>
        public StructuralMatOpenings Openings;
        public string Mark;
        public bool Required = true;
        public JObject Raw;
    }

    public sealed class StructuralRebarRule
    {
        public string Id;
        public StructuralHostSelector Host = new StructuralHostSelector();
        public string BarTypeId;
        public string ShapeName;
        /// <summary>standard or stirrup_tie. Declared, never inferred from the curves.</summary>
        public string Style = StructuralStyle.Standard;
        /// <summary>Explicit centreline in millimetres, model coordinates: [[x,y,z], ...].</summary>
        public List<double[]> CurvesMm = new List<double[]>();
        public bool Closed;
        public double[] NormalMm;
        public RebarLayoutRequest Layout = new RebarLayoutRequest();
        public StructuralTermination Start = new StructuralTermination();
        public StructuralTermination End = new StructuralTermination();
        public string Mark;
        public bool Required = true;
        /// <summary>
        /// May Revit CREATE a new rebar shape family to hold this bar? False by
        /// default and never inferred: a shape appears in the project browser, in
        /// schedules and in everybody else's model, and adding one silently is a
        /// change nobody asked for.
        /// </summary>
        public bool AllowNewShape;
        /// <summary>Which side of the bar the set marches to. Revit's own default is true.</summary>
        public bool BarsOnNormalSide = true;
        /// <summary>
        /// Set ONLY by a cover-aware stirrup zone expansion: what the planner
        /// predicted from the cover, so the apply can hold the model to it. Never
        /// parsed from a set.
        /// </summary>
        public StirrupCoverPrediction CoverPrediction;
        /// <summary>
        /// Set ONLY by a mat expansion over a host with openings: the openings the
        /// bars were planned around, so the apply can check the drawn bars against
        /// the same regions. Never parsed from a set.
        /// </summary>
        public MatOpeningContext OpeningContext;
        public JObject Raw;
    }

    public static class StructuralStyle
    {
        public const string Standard = "standard";
        public const string StirrupTie = "stirrup_tie";
        public static readonly string[] All = { Standard, StirrupTie };
        public static bool IsKnown(string s)
        {
            return s == Standard || s == StirrupTie;
        }
    }

    /// <summary>
    /// The orientation words, repeated here so Core can validate them without
    /// referencing the Revit-touching shim that maps them.
    /// </summary>
    public static class RebarApiVocabulary
    {
        public const string OrientationLeft = "left";
        public const string OrientationRight = "right";
        public static readonly string[] All = { OrientationLeft, OrientationRight };
        public static bool IsKnown(string s)
        {
            return s == OrientationLeft || s == OrientationRight;
        }
    }

    public sealed class StructuralTolerances
    {
        public double LengthMm = 2.0;
        public double SpacingMm = 2.0;
        public double CoverMm = 1.0;
        public double AngleDegrees = 1.0;
    }

    public sealed class StructuralRequirementSet
    {
        public const string SchemaName = "horizun.structural-requirements/1";

        public string Id;
        public string Version;
        public string Title;
        public StructuralTolerances Tolerances = new StructuralTolerances();
        public Dictionary<string, StructuralBarTypeRef> BarTypes =
            new Dictionary<string, StructuralBarTypeRef>(StringComparer.Ordinal);
        public Dictionary<string, StructuralHookTypeRef> HookTypes =
            new Dictionary<string, StructuralHookTypeRef>(StringComparer.Ordinal);
        public List<StructuralCoverRule> CoverRules = new List<StructuralCoverRule>();
        public List<StructuralRebarRule> RebarRules = new List<StructuralRebarRule>();
        public List<StructuralStirrupZoneRule> StirrupZoneRules = new List<StructuralStirrupZoneRule>();
        public List<StructuralMatRule> MatRules = new List<StructuralMatRule>();

        public string Error;
        public string Code;
        public bool Ok { get { return Error == null; } }

        // Refusal codes, closed.
        public const string CodeSchema = "schema_not_recognised";
        public const string CodeMissing = "required_field_missing";
        public const string CodeUnknownValue = "value_not_in_vocabulary";
        public const string CodeDuplicateId = "duplicate_id";
        public const string CodeUnresolvedReference = "reference_not_declared";
        public const string CodeGeometry = "geometry_not_usable";
        public const string CodeLayout = "layout_not_usable";
        public const string CodeNoRules = "no_rules";
        public const string CodeUnits = "units_not_millimetres";
        public const string CodeNotFinite = "value_is_not_a_finite_number";
        public const string CodeNotANumber = "value_is_not_a_number";
        public const string CodeUnknownKey = "unknown_key";

        /// <summary>
        /// The exact field the refusal is about, as a path - "stirrup_zone_rules['B1'].zones[0].lenght_mm" -
        /// so a caller can locate it by machine rather than by reading the message. Null when the
        /// refusal is about the set as a whole.
        /// </summary>
        public string Path;

        /// <summary>
        /// For an unknown key: every key that IS admitted at that place, in ordinal order, so the
        /// caller can pick the right one. Empty for every other refusal.
        /// </summary>
        public List<string> Allowed = new List<string>();

        /// <summary>
        /// For an unknown key: the admitted keys that look like what was typed, closest first.
        /// A misspelling lands here; an invented key does not.
        /// </summary>
        public List<string> DidYouMean = new List<string>();

        // EVERY OBJECT SHAPE IN THIS SCHEMA NAMES ITS KEYS, and a key outside the
        // list refuses the whole set. A misspelt key used to be read as an absent
        // one: `lenght_mm` on a zone silently became "the rest of the span",
        // `start_offset` silently became 0, `matt_rules` silently became a set with
        // no mats - and the plan that came back looked complete. The three sibling
        // parsers in this folder refuse unknown keys; this one now does too.
        public static readonly HashSet<string> RootKeys = Keys(
            "schema", "units", "requirement_set", "tolerances", "bar_types", "hook_types",
            "cover_rules", "reinforcement_rules", "stirrup_zone_rules", "mat_rules");
        public static readonly HashSet<string> HeaderKeys = Keys("id", "version", "title");
        public static readonly HashSet<string> ToleranceKeys = Keys("length_mm", "spacing_mm", "cover_mm", "angle_degrees");
        public static readonly HashSet<string> BarTypeKeys = Keys("id", "type_name", "nominal_diameter_mm");
        public static readonly HashSet<string> HookTypeKeys = Keys("id", "type_name", "none");
        public static readonly HashSet<string> CoverRuleKeys = Keys(
            "id", "host", "face", "cover_type_name", "distance_mm", "required");
        public static readonly HashSet<string> HostKeys = Keys("category", "type_name", "element_ids");
        public static readonly HashSet<string> RebarRuleKeys = Keys(
            "id", "host", "bar_type", "shape", "allow_new_shape", "style", "curve_mm", "closed", "normal",
            "layout", "start", "end", "mark", "required");
        public static readonly HashSet<string> LayoutKeys = Keys(
            "rule", "number", "spacing_mm", "array_length_mm", "include_first_bar", "include_last_bar",
            "bars_on_normal_side");
        public static readonly HashSet<string> TerminationKeys = Keys("hook_type", "orientation");
        public static readonly HashSet<string> StirrupZoneRuleKeys = Keys(
            "id", "host", "bar_type", "shape", "allow_new_shape", "style", "profile_mm", "closed", "along",
            "span_mm", "span", "start_offset_mm", "end_offset_mm", "symmetric",
            "minimum_clear_between_zones_mm", "cover", "zones", "start", "end", "mark", "required");
        public static readonly HashSet<string> ZoneKeys = Keys("name", "length_mm", "mark", "layout");
        public static readonly HashSet<string> ZoneCoverKeys = Keys("source", "distance_mm");
        public static readonly HashSet<string> MatRuleKeys = Keys(
            "id", "host", "face_normal", "components", "openings", "mark", "required");
        public static readonly HashSet<string> MatOpeningsKeys = Keys("policy", "minimum_size_mm", "clearance_mm");
        public static readonly HashSet<string> MatComponentKeys = Keys(
            "name", "mark", "shape", "allow_new_shape", "bar_type", "direction", "offset_from_face_mm",
            "end_cover_mm", "side_cover_mm", "layout");

        private static HashSet<string> Keys(params string[] names)
        {
            return new HashSet<string>(names, StringComparer.Ordinal);
        }

        /// <summary>
        /// The largest tolerance this accepts. A tolerance is the width of the band
        /// in which two measurements count as the same, and one of a metre means
        /// nothing can ever disagree - the mirror image of the zero the parser
        /// already refuses, and the more dangerous of the two, because it makes an
        /// audit report agreement rather than failure.
        /// </summary>
        public const double MaxToleranceMm = 500.0;

        /// <summary>
        /// The machine-readable half of a refusal, for the three commands that load a set:
        /// code, schema, the exact field path when there is one, and for an unknown key
        /// the admitted alternatives. One shape, so a caller locates the field the same way
        /// whether it planned, applied or audited.
        /// </summary>
        public static JObject RefusalDetail(StructuralRequirementSet set)
        {
            var d = new JObject
            {
                ["code"] = set.Code,
                ["schema"] = SchemaName,
                ["path"] = set.Path
            };
            if (set.Allowed.Count > 0) d["allowed"] = new JArray(set.Allowed);
            if (set.DidYouMean.Count > 0) d["did_you_mean"] = new JArray(set.DidYouMean);
            return d;
        }

        /// <summary>Parse and validate. Returns a set whose Ok is false rather than throwing.</summary>
        public static StructuralRequirementSet Load(JObject doc)
        {
            var s = new StructuralRequirementSet();
            if (doc == null) return Fail(s, CodeSchema, "the requirement set is not a JSON object.");
            if (UnknownKey(s, doc, RootKeys, null) != null) return s;

            string schema = doc.Value<string>("schema");
            if (!string.Equals(schema, SchemaName, StringComparison.Ordinal))
                return Fail(s, CodeSchema,
                    "schema must be '" + SchemaName + "' - got " + Show(schema) + ". The version is part of the " +
                    "contract: a set written for another schema may mean something different by the same words.");

            // MILLIMETRES, and only millimetres. Accepting a units field with more
            // than one legal value would mean every number in every rule had to be
            // read together with it, and a set that lost the field on the way would
            // be read as feet without anything looking wrong.
            string units = doc.Value<string>("units");
            if (units != null && !string.Equals(units, "millimeter", StringComparison.Ordinal))
                return Fail(s, CodeUnits,
                    "units must be 'millimeter' or absent - got " + Show(units) + ". Every length in this " +
                    "schema is millimetres by definition.");

            JObject header = doc["requirement_set"] as JObject;
            if (header == null) return Fail(s, CodeMissing, "requirement_set is required and must be an object.");
            if (UnknownKey(s, header, HeaderKeys, "requirement_set") != null) return s;
            s.Id = header.Value<string>("id");
            s.Version = header.Value<string>("version");
            s.Title = header.Value<string>("title");
            if (string.IsNullOrWhiteSpace(s.Id)) return Fail(s, CodeMissing, "requirement_set.id is required.");
            if (string.IsNullOrWhiteSpace(s.Version)) return Fail(s, CodeMissing, "requirement_set.version is required.");

            JObject tol = doc["tolerances"] as JObject;
            if (tol != null)
            {
                if (UnknownKey(s, tol, ToleranceKeys, "tolerances") != null) return s;
                s.Tolerances.LengthMm = tol.Value<double?>("length_mm") ?? s.Tolerances.LengthMm;
                s.Tolerances.SpacingMm = tol.Value<double?>("spacing_mm") ?? s.Tolerances.SpacingMm;
                s.Tolerances.CoverMm = tol.Value<double?>("cover_mm") ?? s.Tolerances.CoverMm;
                s.Tolerances.AngleDegrees = tol.Value<double?>("angle_degrees") ?? s.Tolerances.AngleDegrees;
                foreach (var pair in new[]
                         {
                             new { n = "length_mm", v = s.Tolerances.LengthMm },
                             new { n = "spacing_mm", v = s.Tolerances.SpacingMm },
                             new { n = "cover_mm", v = s.Tolerances.CoverMm },
                             new { n = "angle_degrees", v = s.Tolerances.AngleDegrees }
                         })
                {
                    if (!RebarLayoutRules.IsFinite(pair.v))
                        return Fail(s, CodeNotFinite,
                            "tolerances." + pair.n + " is not a finite number. Every comparison against NaN is " +
                            "false, so a tolerance like this makes every measurement agree with every other one " +
                            "and an audit can never report a difference again.");
                    if (pair.v <= 0)
                        return Fail(s, CodeUnknownValue,
                            "tolerances." + pair.n + " must be greater than zero - a tolerance of zero means no " +
                            "measurement can ever agree, which is not what anybody means by it.");
                    if (pair.v > MaxToleranceMm)
                        return Fail(s, CodeUnknownValue,
                            "tolerances." + pair.n + " is " + pair.v.ToString("0.###", CultureInfo.InvariantCulture) +
                            ", and the limit is " + MaxToleranceMm + ". A tolerance that wide is not a tolerance: " +
                            "nothing measured could ever disagree with anything declared.");
                }
            }

            // ------------------------------------------------------- bar types
            foreach (JToken t in doc["bar_types"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in bar_types must be an object.");
                string id = o.Value<string>("id");
                if (UnknownKey(s, o, BarTypeKeys, "bar_types[" + s.BarTypes.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(id)) return Fail(s, CodeMissing, "every bar_types entry needs an id.");
                if (s.BarTypes.ContainsKey(id)) return Fail(s, CodeDuplicateId, "bar_types has two entries with id '" + id + "'.");
                string typeName = o.Value<string>("type_name");
                if (string.IsNullOrWhiteSpace(typeName))
                    return Fail(s, CodeMissing,
                        "bar_types['" + id + "'] needs type_name: the name of a RebarBarType that exists in the " +
                        "model. This bridge does not create bar types, because a bar type carries a diameter, a " +
                        "bend radius and a grade, and inventing those is designing.");
                double? dia = o.Value<double?>("nominal_diameter_mm");
                if (dia.HasValue && (!RebarLayoutRules.IsFinite(dia.Value) || dia.Value <= 0))
                    return Fail(s, CodeUnknownValue,
                        "bar_types['" + id + "'].nominal_diameter_mm must be a finite number greater than zero.");
                s.BarTypes[id] = new StructuralBarTypeRef
                {
                    Id = id,
                    TypeName = typeName,
                    NominalDiameterMm = dia
                };
            }

            // ------------------------------------------------------ hook types
            foreach (JToken t in doc["hook_types"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in hook_types must be an object.");
                string id = o.Value<string>("id");
                if (UnknownKey(s, o, HookTypeKeys, "hook_types[" + s.HookTypes.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(id)) return Fail(s, CodeMissing, "every hook_types entry needs an id.");
                if (s.HookTypes.ContainsKey(id)) return Fail(s, CodeDuplicateId, "hook_types has two entries with id '" + id + "'.");
                string typeName = o.Value<string>("type_name");
                bool none = o.Value<bool?>("none") ?? false;
                if (!none && string.IsNullOrWhiteSpace(typeName))
                    return Fail(s, CodeMissing,
                        "hook_types['" + id + "'] needs type_name, or none: true to mean an explicitly straight end.");
                s.HookTypes[id] = new StructuralHookTypeRef { Id = id, TypeName = typeName, None = none };
            }

            // ----------------------------------------------------- cover rules
            var coverIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in doc["cover_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in cover_rules must be an object.");
                var r = new StructuralCoverRule { Id = o.Value<string>("id") };
                if (UnknownKey(s, o, CoverRuleKeys, "cover_rules[" + s.CoverRules.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(r.Id)) return Fail(s, CodeMissing, "every cover rule needs an id.");
                if (!coverIds.Add(r.Id)) return Fail(s, CodeDuplicateId, "two cover rules share the id '" + r.Id + "'.");
                if (UnknownKey(s, o["host"] as JObject, HostKeys, "cover_rules['" + r.Id + "'].host") != null) return s;
                string err = ReadSelector(o["host"] as JObject, r.Host, "cover_rules['" + r.Id + "'].host");
                if (err != null) return Fail(s, CodeMissing, err, "cover_rules['" + r.Id + "'].host");
                r.Face = (o.Value<string>("face") ?? "common").Trim();
                if (!string.Equals(r.Face, "common", StringComparison.Ordinal))
                    return Fail(s, CodeUnknownValue,
                        "cover_rules['" + r.Id + "'].face is '" + r.Face + "'. Only 'common' is implemented: " +
                        "setting cover on ONE face needs a stable way to name that face across a re-issue, and " +
                        "this bridge does not have one yet. Per-face cover is READ by " +
                        "horizun_query_structure mode=hosts and is not written.");
                r.CoverTypeName = o.Value<string>("cover_type_name");
                r.DistanceMm = o.Value<double?>("distance_mm");
                if (string.IsNullOrWhiteSpace(r.CoverTypeName) && !r.DistanceMm.HasValue)
                    return Fail(s, CodeMissing,
                        "cover_rules['" + r.Id + "'] must name a cover_type_name, a distance_mm, or both. A cover " +
                        "this bridge picked would be a design decision.");
                if (r.DistanceMm.HasValue && !RebarLayoutRules.IsFinite(r.DistanceMm.Value))
                    return Fail(s, CodeNotFinite, "cover_rules['" + r.Id + "'].distance_mm is not a finite number.");
                if (r.DistanceMm.HasValue && r.DistanceMm.Value <= 0)
                    return Fail(s, CodeUnknownValue, "cover_rules['" + r.Id + "'].distance_mm must be greater than zero.");
                r.Required = o.Value<bool?>("required") ?? true;
                s.CoverRules.Add(r);
            }

            // ----------------------------------------------------- rebar rules
            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken t in doc["reinforcement_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) return Fail(s, CodeSchema, "every entry in reinforcement_rules must be an object.");
                var r = new StructuralRebarRule { Id = o.Value<string>("id"), Raw = o };
                if (UnknownKey(s, o, RebarRuleKeys, "reinforcement_rules[" + s.RebarRules.Count + "]") != null) return s;
                if (string.IsNullOrWhiteSpace(r.Id)) return Fail(s, CodeMissing, "every reinforcement rule needs an id.");
                if (!ruleIds.Add(r.Id)) return Fail(s, CodeDuplicateId, "two reinforcement rules share the id '" + r.Id + "'.");

                if (UnknownKey(s, o["host"] as JObject, HostKeys, "reinforcement_rules['" + r.Id + "'].host") != null) return s;
                string err = ReadSelector(o["host"] as JObject, r.Host, "reinforcement_rules['" + r.Id + "'].host");
                if (err != null) return Fail(s, CodeMissing, err, "reinforcement_rules['" + r.Id + "'].host");

                r.BarTypeId = o.Value<string>("bar_type");
                if (string.IsNullOrWhiteSpace(r.BarTypeId))
                    return Fail(s, CodeMissing, "reinforcement_rules['" + r.Id + "'] needs bar_type.");
                if (!s.BarTypes.ContainsKey(r.BarTypeId))
                    return Fail(s, CodeUnresolvedReference,
                        "reinforcement_rules['" + r.Id + "'].bar_type is '" + r.BarTypeId +
                        "', which no bar_types entry declares.");

                r.ShapeName = o.Value<string>("shape");
                // DECLARED, not defaulted. It used to fall back to `standard`,
                // which is a design decision taken in silence - and the
                // requirement-set documentation lists this field as declared, so the
                // code and the document disagreed about it.
                if (o["style"] == null)
                    return Fail(s, CodeMissing,
                        "reinforcement_rules['" + r.Id + "'] needs style: " +
                        string.Join(" or ", StructuralStyle.All) +
                        ". It is not defaulted - a closed rectangle is a stirrup in a beam and an edge bar in a " +
                        "slab, and the geometry does not say which.");
                r.Style = (o.Value<string>("style") ?? "").Trim();
                if (!StructuralStyle.IsKnown(r.Style))
                    return Fail(s, CodeUnknownValue,
                        "reinforcement_rules['" + r.Id + "'].style must be " + string.Join(" or ", StructuralStyle.All) +
                        " - got " + Show(r.Style) + ". The style is declared and never inferred from the shape of " +
                        "the curves: a closed rectangle is a stirrup in a beam and a slab edge bar in a slab, and " +
                        "the geometry does not say which.");

                string gcode;
                string gerr = ReadCurves(o, r, out gcode);
                if (gerr != null) return Fail(s, gcode, gerr);

                JArray normal = o["normal"] as JArray;
                if (normal == null)
                    return Fail(s, CodeMissing,
                        "reinforcement_rules['" + r.Id + "'] needs normal: the direction the SET marches in, as " +
                        "[x, y, z]. It is not derivable from one bar's curves - the same bar distributes along a " +
                        "beam or up a column depending on it - so it is declared.");
                if (normal.Count != 3)
                    return Fail(s, CodeGeometry, "reinforcement_rules['" + r.Id + "'].normal must have three numbers.");
                var nrm = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (normal[i].Type != JTokenType.Integer && normal[i].Type != JTokenType.Float)
                        return Fail(s, CodeNotANumber, "reinforcement_rules['" + r.Id + "'].normal carries " +
                            normal[i].ToString(Newtonsoft.Json.Formatting.None) + ", which is not a number.");
                    nrm[i] = normal[i].Value<double>();
                    // A NaN normal passes the zero-vector test - Math.Abs(NaN) is NaN
                    // and NaN < 1e-9 is false - and then collapses every projection
                    // downstream to a span of nothing.
                    if (!RebarLayoutRules.IsFinite(nrm[i]))
                        return Fail(s, CodeNotFinite,
                            "reinforcement_rules['" + r.Id + "'].normal carries a value that is not finite.");
                }
                r.NormalMm = nrm;
                if (Math.Abs(r.NormalMm[0]) + Math.Abs(r.NormalMm[1]) + Math.Abs(r.NormalMm[2]) < 1e-9)
                    return Fail(s, CodeGeometry, "reinforcement_rules['" + r.Id + "'].normal is the zero vector.");

                string rat = "reinforcement_rules['" + r.Id + "']";
                if (UnknownKey(s, o["layout"] as JObject, LayoutKeys, rat + ".layout") != null) return s;
                string lerr = ReadLayout(o["layout"] as JObject, r, s);
                if (lerr != null) return Fail(s, CodeLayout, lerr, rat + ".layout");

                if (UnknownKey(s, o["start"] as JObject, TerminationKeys, rat + ".start") != null) return s;
                if (UnknownKey(s, o["end"] as JObject, TerminationKeys, rat + ".end") != null) return s;
                string terr = ReadTermination(o["start"] as JObject, r.Start, s, r.Id, "start");
                if (terr != null) return Fail(s, CodeUnknownValue, terr, rat + ".start");
                terr = ReadTermination(o["end"] as JObject, r.End, s, r.Id, "end");
                if (terr != null) return Fail(s, CodeUnknownValue, terr, rat + ".end");

                r.Mark = o.Value<string>("mark");
                r.Required = o.Value<bool?>("required") ?? true;
                r.AllowNewShape = o.Value<bool?>("allow_new_shape") ?? false;
                if (r.AllowNewShape && !string.IsNullOrWhiteSpace(r.ShapeName))
                    return Fail(s, CodeUnknownValue,
                        "reinforcement_rules['" + r.Id + "'] names a shape AND sets allow_new_shape. Those are " +
                        "two different instructions - use the shape that exists, or let Revit make one - and " +
                        "which was meant cannot be read from the set.");
                s.RebarRules.Add(r);
            }

            string zerr = ReadStirrupZoneRules(doc, s, ruleIds);
            if (zerr != null) return s;

            string merr = ReadMatRules(doc, s, ruleIds);
            if (merr != null) return s;

            if (s.CoverRules.Count == 0 && s.RebarRules.Count == 0 && s.StirrupZoneRules.Count == 0 &&
                s.MatRules.Count == 0)
                return Fail(s, CodeNoRules,
                    "the set declares no cover_rules, no reinforcement_rules, no stirrup_zone_rules and no " +
                    "mat_rules, so it asks for nothing.");
            return s;
        }

        /// <summary>
        /// stirrup_zone_rules. Returns null on success, or a message when the set is
        /// already failed - the failure itself is recorded on `s`.
        /// </summary>
        private static string ReadStirrupZoneRules(JObject doc, StructuralRequirementSet s,
                                                   HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["stirrup_zone_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in stirrup_zone_rules must be an object."); return "x"; }
                var z = new StructuralStirrupZoneRule { Id = o.Value<string>("id"), Raw = o };
                string at = "stirrup_zone_rules['" + z.Id + "']";
                if (UnknownKey(s, o, StirrupZoneRuleKeys, "stirrup_zone_rules[" + s.StirrupZoneRules.Count + "]") != null) return "x";
                if (string.IsNullOrWhiteSpace(z.Id)) { Fail(s, CodeMissing, "every stirrup zone rule needs an id."); return "x"; }
                if (!ruleIds.Add(z.Id))
                {
                    Fail(s, CodeDuplicateId,
                        "'" + z.Id + "' is used by more than one rule. A stirrup zone rule expands into one " +
                        "reinforcement rule per zone, named <id>#<zone>, so its id shares the namespace.");
                    return "x";
                }

                if (UnknownKey(s, o["host"] as JObject, HostKeys, at + ".host") != null) return "x";
                string err = ReadSelector(o["host"] as JObject, z.Host, at + ".host");
                if (err != null) { Fail(s, CodeMissing, err, at + ".host"); return "x"; }

                z.BarTypeId = o.Value<string>("bar_type");
                if (string.IsNullOrWhiteSpace(z.BarTypeId)) { Fail(s, CodeMissing, at + " needs bar_type."); return "x"; }
                if (!s.BarTypes.ContainsKey(z.BarTypeId))
                {
                    Fail(s, CodeUnresolvedReference,
                        at + ".bar_type is '" + z.BarTypeId + "', which no bar_types entry declares.");
                    return "x";
                }
                z.ShapeName = o.Value<string>("shape");
                z.AllowNewShape = o.Value<bool?>("allow_new_shape") ?? false;
                if (z.AllowNewShape && !string.IsNullOrWhiteSpace(z.ShapeName))
                {
                    Fail(s, CodeUnknownValue,
                        at + " names a shape AND sets allow_new_shape. Those are two different instructions.");
                    return "x";
                }

                // Style DEFAULTS to stirrup_tie here and only here, because the rule
                // is called stirrup_zone_rules. A rule that wants standard bars in
                // zones says so, and that is a different declaration from silence.
                if (o["style"] != null)
                {
                    z.Style = (o.Value<string>("style") ?? "").Trim();
                    if (!StructuralStyle.IsKnown(z.Style))
                    {
                        Fail(s, CodeUnknownValue, at + ".style must be " + string.Join(" or ", StructuralStyle.All));
                        return "x";
                    }
                }

                string perr = ReadPointList(o["profile_mm"] as JArray, at + ".profile_mm", z.ProfileMm);
                if (perr != null) { Fail(s, CodeGeometry, perr); return "x"; }
                if (z.ProfileMm.Count < 2)
                {
                    Fail(s, CodeGeometry,
                        at + ".profile_mm needs at least two points: it is the stirrup outline at the START of " +
                        "the run, in model coordinates, and every zone is that outline moved along the beam.");
                    return "x";
                }
                z.Closed = o.Value<bool?>("closed") ?? true;

                // THE SAME GUARDS THE REINFORCEMENT PATH HAS. profile_mm was read by
                // a generic point-list reader that checks only that the numbers are
                // numbers, so a zero-length segment - and a closed profile that also
                // repeats its first point - reached Revit, which refused it as
                // curve_degenerate at APPLY time. By then the rehearsal had already
                // issued a confirmation token for it. Measured live on 2026-08-28:
                // three zones, three curve_degenerate refusals, after a plan that
                // reported the rule resolved.
                for (int i = 1; i < z.ProfileMm.Count; i++)
                    if (Distance(z.ProfileMm[i - 1], z.ProfileMm[i]) < 1e-6)
                    {
                        Fail(s, CodeGeometry,
                            at + ".profile_mm has two identical consecutive points at index " + (i - 1) +
                            " and " + i + ", which is a segment of zero length.");
                        return "x";
                    }
                if (z.Closed && z.ProfileMm.Count > 1 &&
                    Distance(z.ProfileMm[z.ProfileMm.Count - 1], z.ProfileMm[0]) < 1e-6)
                {
                    Fail(s, CodeGeometry,
                        at + " is closed and also repeats its first point at the end. Declare the corners " +
                        "once; closed adds the last segment.");
                    return "x";
                }

                string aerr = ReadDirection(o["along"] as JArray, at + ".along", out z.AlongMm);
                if (aerr != null) { Fail(s, CodeGeometry, aerr); return "x"; }

                // The span. Declared, or measured from the host by the resolver -
                // which is the only one of the two that can see the model.
                JToken span = o["span_mm"];
                string spanWord = o.Value<string>("span");
                if (span != null && spanWord != null)
                {
                    Fail(s, CodeUnknownValue, at + " declares both span_mm and span. State one.");
                    return "x";
                }
                if (span != null)
                {
                    if (span.Type != JTokenType.Integer && span.Type != JTokenType.Float)
                    { Fail(s, CodeNotANumber, at + ".span_mm is not a number."); return "x"; }
                    double v = span.Value<double>();
                    if (!RebarLayoutRules.IsFinite(v) || v <= 0)
                    { Fail(s, CodeNotFinite, at + ".span_mm must be a positive finite length."); return "x"; }
                    z.SpanMm = v;
                }
                else if (spanWord == "host_length") z.SpanFromHost = true;
                else if (spanWord != null)
                {
                    Fail(s, CodeUnknownValue,
                        at + ".span is '" + spanWord + "'. The only word is host_length; otherwise state span_mm.");
                    return "x";
                }
                else
                {
                    Fail(s, CodeMissing,
                        at + " needs span_mm, or span: host_length to measure the host's location curve. " +
                        "The zones have to run along something and this bridge does not guess which.");
                    return "x";
                }

                z.StartOffsetMm = ReadNonNegative(o["start_offset_mm"], 0);
                z.EndOffsetMm = ReadNonNegative(o["end_offset_mm"], 0);
                if (double.IsNaN(z.StartOffsetMm) || double.IsNaN(z.EndOffsetMm))
                { Fail(s, CodeNotFinite, at + " has an offset that is not a finite, non-negative number."); return "x"; }
                z.Symmetric = o.Value<bool?>("symmetric") ?? false;

                JToken minClear = o["minimum_clear_between_zones_mm"];
                if (minClear != null && minClear.Type != JTokenType.Null)
                {
                    if (minClear.Type != JTokenType.Integer && minClear.Type != JTokenType.Float)
                    { Fail(s, CodeNotANumber, at + ".minimum_clear_between_zones_mm is not a number."); return "x"; }
                    double v = minClear.Value<double>();
                    if (!RebarLayoutRules.IsFinite(v) || v < 0)
                    { Fail(s, CodeNotFinite, at + ".minimum_clear_between_zones_mm must be finite and not negative."); return "x"; }
                    z.MinimumClearBetweenZonesMm = v;
                }

                // THE COVER BLOCK. Declared in full or absent in full: a source
                // with no distance, or a distance beside source: host, are two
                // statements that could disagree, and the parser will not pick.
                JToken coverTok = o["cover"];
                if (coverTok != null && coverTok.Type != JTokenType.Null)
                {
                    var co = coverTok as JObject;
                    if (co == null)
                    {
                        Fail(s, CodeSchema, at + ".cover must be an object: { source: host | declared, distance_mm }.", at + ".cover");
                        return "x";
                    }
                    if (UnknownKey(s, co, ZoneCoverKeys, at + ".cover") != null) return "x";
                    var cover = new StructuralStirrupZoneCover { Source = (co.Value<string>("source") ?? "").Trim() };
                    if (Array.IndexOf(StructuralStirrupZoneCover.Sources, cover.Source) < 0)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".cover.source must be " + string.Join(" or ", StructuralStirrupZoneCover.Sources) +
                            " - got " + Show(co.Value<string>("source")) + ". 'host' reads the host's common cover " +
                            "when the rule is resolved; 'declared' takes distance_mm. Revit clamps a hosted array " +
                            "to the host's cover plus the bar radius at each end, and the zone planner has to be " +
                            "told which number to predict with.", at + ".cover.source");
                        return "x";
                    }
                    string derr;
                    double? dist = ReadNumber(co["distance_mm"], at + ".cover.distance_mm", out derr);
                    if (derr != null) { Fail(s, CodeNotANumber, derr, at + ".cover.distance_mm"); return "x"; }
                    if (cover.Source == StructuralStirrupZoneCover.SourceDeclared)
                    {
                        if (!dist.HasValue)
                        {
                            Fail(s, CodeMissing,
                                at + ".cover declares source: declared and no distance_mm. The distance is the " +
                                "whole declaration; nothing here supplies one.", at + ".cover.distance_mm");
                            return "x";
                        }
                        if (dist.Value < 0)
                        {
                            Fail(s, CodeUnknownValue, at + ".cover.distance_mm must be zero or more.", at + ".cover.distance_mm");
                            return "x";
                        }
                        cover.DistanceMm = dist;
                    }
                    else if (dist.HasValue)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".cover declares source: host AND a distance_mm. The host's cover is read from " +
                            "the model when the rule is resolved, and a number beside it could disagree with " +
                            "what is read; state one or the other.", at + ".cover.distance_mm");
                        return "x";
                    }
                    z.Cover = cover;
                }

                var zonesArr = o["zones"] as JArray;
                if (zonesArr == null || zonesArr.Count == 0)
                {
                    Fail(s, CodeMissing,
                        at + " needs zones: a list of {name, length_mm, layout}. One of them may leave length_mm " +
                        "out, and that one is the rest of the span.");
                    return "x";
                }
                foreach (JToken zt in zonesArr)
                {
                    var zo = zt as JObject;
                    if (zo == null) { Fail(s, CodeSchema, at + ".zones must contain objects."); return "x"; }
                    string zat = at + ".zones[" + z.Zones.Count + "]";
                    if (UnknownKey(s, zo, ZoneKeys, zat) != null) return "x";
                    if (UnknownKey(s, zo["layout"] as JObject, LayoutKeys, zat + ".layout") != null) return "x";
                    var req = new StirrupZoneRequest { Name = zo.Value<string>("name"), Mark = zo.Value<string>("mark") };
                    JToken len = zo["length_mm"];
                    if (len != null && len.Type != JTokenType.Null)
                    {
                        if (len.Type != JTokenType.Integer && len.Type != JTokenType.Float)
                        { Fail(s, CodeNotANumber, at + ".zones length_mm is not a number."); return "x"; }
                        req.LengthMm = len.Value<double>();
                    }
                    // ReadLayout looks the bar type up to seed the diameter that
                    // minimum_clear_spacing counts with, so the holder carries it.
                    var holder = new StructuralRebarRule { Id = z.Id, BarTypeId = z.BarTypeId };
                    string lerr = ReadLayout(zo["layout"] as JObject, holder, s, false,
                                             at + ".zones['" + (req.Name ?? "?") + "']");
                    if (lerr != null) { Fail(s, CodeLayout, lerr); return "x"; }
                    req.Layout = holder.Layout;

                    // THE EXPANDED ID IS RESERVED TOO. The refusal above promises
                    // that a zone rule expands into <id>#<zone> and that its id
                    // shares the namespace - and only the top-level id was ever
                    // registered, so a reinforcement rule literally called
                    // "B1#start" parsed happily beside a zone rule that expands to
                    // exactly that. Two sets, one rule id, and provenance keys on it.
                    string expandedId = z.Id + "#" + (req.Name ?? ("zone" + (z.Zones.Count + 1)));
                    if (!ruleIds.Add(expandedId))
                    {
                        Fail(s, CodeDuplicateId,
                            "'" + expandedId + "' is used by more than one rule. " + at + " expands into it.");
                        return "x";
                    }
                    z.Zones.Add(req);
                }

                if (UnknownKey(s, o["start"] as JObject, TerminationKeys, at + ".start") != null) return "x";
                if (UnknownKey(s, o["end"] as JObject, TerminationKeys, at + ".end") != null) return "x";
                string terr = ReadTermination(o["start"] as JObject, z.Start, s, z.Id, "start");
                if (terr != null) { Fail(s, CodeUnknownValue, terr, at + ".start"); return "x"; }
                terr = ReadTermination(o["end"] as JObject, z.End, s, z.Id, "end");
                if (terr != null) { Fail(s, CodeUnknownValue, terr, at + ".end"); return "x"; }

                z.Mark = o.Value<string>("mark");
                z.Required = o.Value<bool?>("required") ?? true;
                s.StirrupZoneRules.Add(z);
            }
            return null;
        }

        /// <summary>mat_rules. Returns null on success; the failure is recorded on `s`.</summary>
        private static string ReadMatRules(JObject doc, StructuralRequirementSet s, HashSet<string> ruleIds)
        {
            foreach (JToken t in doc["mat_rules"] as JArray ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) { Fail(s, CodeSchema, "every entry in mat_rules must be an object."); return "x"; }
                var m = new StructuralMatRule { Id = o.Value<string>("id"), Raw = o };
                string at = "mat_rules['" + m.Id + "']";
                if (UnknownKey(s, o, MatRuleKeys, "mat_rules[" + s.MatRules.Count + "]") != null) return "x";
                if (string.IsNullOrWhiteSpace(m.Id)) { Fail(s, CodeMissing, "every mat rule needs an id."); return "x"; }
                if (!ruleIds.Add(m.Id))
                {
                    Fail(s, CodeDuplicateId,
                        "'" + m.Id + "' is used by more than one rule. A mat rule expands into one " +
                        "reinforcement rule per component, named <id>#<component>, so its id shares the namespace.");
                    return "x";
                }

                if (UnknownKey(s, o["host"] as JObject, HostKeys, at + ".host") != null) return "x";
                string err = ReadSelector(o["host"] as JObject, m.Host, at + ".host");
                if (err != null) { Fail(s, CodeMissing, err, at + ".host"); return "x"; }

                string nerr = ReadDirection(o["face_normal"] as JArray, at + ".face_normal", out m.FaceNormalMm);
                if (nerr != null)
                {
                    Fail(s, CodeGeometry, nerr +
                        " It points OUT of the face the mat sits under - [0,0,1] for the top of a slab, " +
                        "[0,0,-1] for the bottom, and the wall's own normal for a wall. It is declared because " +
                        "a slab has two faces and the geometry does not say which one was meant.");
                    return "x";
                }

                m.Mark = o.Value<string>("mark");
                m.Required = o.Value<bool?>("required") ?? true;

                // THE OPENINGS BLOCK. Three declared numbers and words, none of
                // them defaulted: which policy, below what size an opening is
                // ignored, and how far a trimmed bar stops from the edge. A value a
                // policy would not use is refused, on the same principle as a layout
                // that names a spacing it does not use.
                JToken opTok = o["openings"];
                if (opTok != null && opTok.Type != JTokenType.Null)
                {
                    var oo = opTok as JObject;
                    if (oo == null)
                    {
                        Fail(s, CodeSchema, at + ".openings must be an object: { policy, minimum_size_mm, clearance_mm }.", at + ".openings");
                        return "x";
                    }
                    if (UnknownKey(s, oo, MatOpeningsKeys, at + ".openings") != null) return "x";
                    var op = new StructuralMatOpenings { Policy = (oo.Value<string>("policy") ?? "").Trim() };
                    if (Array.IndexOf(StructuralMatOpenings.Policies, op.Policy) < 0)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".openings.policy must be " + string.Join(", ", StructuralMatOpenings.Policies) +
                            " - got " + Show(oo.Value<string>("policy")) + ". omit drops every bar that would " +
                            "cross an opening; trim stops the bar short of it on each side; ignore builds the " +
                            "bars as declared and reports the crossings.", at + ".openings.policy");
                        return "x";
                    }
                    string oerr;
                    double? min = ReadNumber(oo["minimum_size_mm"], at + ".openings.minimum_size_mm", out oerr);
                    if (oerr != null) { Fail(s, CodeNotANumber, oerr, at + ".openings.minimum_size_mm"); return "x"; }
                    if (!min.HasValue)
                    {
                        Fail(s, CodeMissing,
                            at + ".openings needs minimum_size_mm: openings whose largest dimension is below it " +
                            "are ignored. It is declared, not defaulted - a sleeve a bar may run past and a " +
                            "shaft it may not are the same shape at different sizes, and where the line sits " +
                            "is a decision.", at + ".openings.minimum_size_mm");
                        return "x";
                    }
                    if (min.Value < 0)
                    {
                        Fail(s, CodeUnknownValue, at + ".openings.minimum_size_mm must be zero or more.", at + ".openings.minimum_size_mm");
                        return "x";
                    }
                    op.MinimumSizeMm = min.Value;
                    double? clear = ReadNumber(oo["clearance_mm"], at + ".openings.clearance_mm", out oerr);
                    if (oerr != null) { Fail(s, CodeNotANumber, oerr, at + ".openings.clearance_mm"); return "x"; }
                    if (op.Policy == StructuralMatOpenings.PolicyTrim)
                    {
                        if (!clear.HasValue)
                        {
                            Fail(s, CodeMissing,
                                at + ".openings declares policy: trim and no clearance_mm - how far short of the " +
                                "opening a trimmed bar stops. Nothing here supplies one.", at + ".openings.clearance_mm");
                            return "x";
                        }
                        if (clear.Value < 0)
                        {
                            Fail(s, CodeUnknownValue, at + ".openings.clearance_mm must be zero or more.", at + ".openings.clearance_mm");
                            return "x";
                        }
                        op.ClearanceMm = clear;
                    }
                    else if (clear.HasValue)
                    {
                        Fail(s, CodeUnknownValue,
                            at + ".openings declares policy: " + op.Policy + " and a clearance_mm beside it. Only " +
                            "trim stops a bar short of an opening, so the number would be ignored - and somebody " +
                            "wrote it meaning something.", at + ".openings.clearance_mm");
                        return "x";
                    }
                    m.Openings = op;
                }

                var comps = o["components"] as JArray;
                if (comps == null || comps.Count == 0)
                {
                    Fail(s, CodeMissing,
                        at + " needs components: a list of {name, direction, bar_type, offset_from_face_mm, " +
                        "layout}. Nothing here invents a mat.");
                    return "x";
                }
                foreach (JToken ct in comps)
                {
                    var co = ct as JObject;
                    if (co == null) { Fail(s, CodeSchema, at + ".components must contain objects."); return "x"; }
                    string cpath = at + ".components[" + m.Components.Count + "]";
                    if (UnknownKey(s, co, MatComponentKeys, cpath) != null) return "x";
                    if (UnknownKey(s, co["layout"] as JObject, LayoutKeys, cpath + ".layout") != null) return "x";
                    var c = new MatComponentRequest
                    {
                        Name = co.Value<string>("name"),
                        Mark = co.Value<string>("mark"),
                        ShapeName = co.Value<string>("shape"),
                        AllowNewShape = co.Value<bool?>("allow_new_shape") ?? false
                    };
                    string cat = at + ".components['" + (c.Name ?? "?") + "']";

                    c.BarTypeId = co.Value<string>("bar_type");
                    if (string.IsNullOrWhiteSpace(c.BarTypeId)) { Fail(s, CodeMissing, cat + " needs bar_type."); return "x"; }
                    if (!s.BarTypes.ContainsKey(c.BarTypeId))
                    {
                        Fail(s, CodeUnresolvedReference,
                            cat + ".bar_type is '" + c.BarTypeId + "', which no bar_types entry declares.");
                        return "x";
                    }

                    string derr = ReadDirection(co["direction"] as JArray, cat + ".direction", out c.DirectionMm);
                    if (derr != null) { Fail(s, CodeGeometry, derr); return "x"; }

                    JToken off = co["offset_from_face_mm"];
                    if (off == null || off.Type == JTokenType.Null)
                    {
                        Fail(s, CodeMissing,
                            cat + " needs offset_from_face_mm: how deep this layer's centreline sits under the " +
                            "face. It is declared rather than derived from a cover, because the second layer of " +
                            "a mat sits under the first by an amount that is a decision.");
                        return "x";
                    }
                    c.OffsetFromFaceMm = ReadNonNegative(off, double.NaN);
                    c.EndCoverMm = ReadNonNegative(co["end_cover_mm"], 0);
                    c.SideCoverMm = ReadNonNegative(co["side_cover_mm"], 0);
                    if (double.IsNaN(c.OffsetFromFaceMm) || double.IsNaN(c.EndCoverMm) || double.IsNaN(c.SideCoverMm))
                    {
                        Fail(s, CodeNotFinite, cat + " has an offset or cover that is not a finite distance of zero or more.");
                        return "x";
                    }

                    var holder = new StructuralRebarRule { Id = m.Id, BarTypeId = c.BarTypeId };
                    string lerr = ReadLayout(co["layout"] as JObject, holder, s, false, cat);
                    if (lerr != null) { Fail(s, CodeLayout, lerr); return "x"; }
                    c.Layout = holder.Layout;

                    string expandedId = m.Id + "#" + (c.Name ?? ("component" + (m.Components.Count + 1)));
                    if (!ruleIds.Add(expandedId))
                    {
                        Fail(s, CodeDuplicateId,
                            "'" + expandedId + "' is used by more than one rule. " + at + " expands into it.");
                        return "x";
                    }
                    m.Components.Add(c);
                }
                s.MatRules.Add(m);
            }
            return null;
        }

        /// <summary>
        /// The first key of `o` that `known` does not admit, or null. When one is found the
        /// set is failed with the exact path, the admitted keys and the closest matches, and
        /// the failed set is returned so the caller can `return`.
        /// </summary>
        private static StructuralRequirementSet UnknownKey(StructuralRequirementSet s, JObject o,
                                                           HashSet<string> known, string at)
        {
            if (o == null) return null;
            foreach (JProperty prop in o.Properties())
            {
                if (known.Contains(prop.Name)) continue;
                string path = string.IsNullOrEmpty(at) ? prop.Name : at + "." + prop.Name;
                var allowed = known.OrderBy(x => x, StringComparer.Ordinal).ToList();
                var near = allowed
                    .Select(k => new { k, d = EditDistance(prop.Name, k) })
                    .Where(x => x.d <= Math.Max(2, prop.Name.Length / 3) || Related(prop.Name, x.k))
                    .OrderBy(x => x.d).ThenBy(x => x.k, StringComparer.Ordinal)
                    .Select(x => x.k).ToList();
                string where = string.IsNullOrEmpty(at) ? "the root of the set" : at;
                Fail(s, CodeUnknownKey,
                     "'" + prop.Name + "' is not a key this schema admits at " + where + "." +
                     (near.Count > 0 ? " Did you mean " + string.Join(" or ", near.Select(k => "'" + k + "'")) + "?" : "") +
                     " Admitted here: " + string.Join(", ", allowed) + ". An unknown key is refused rather " +
                     "than skipped: read as absent it would silently become a default, and the plan would " +
                     "look complete while carrying less than was asked for.",
                     path);
                s.Allowed = allowed;
                s.DidYouMean = near;
                return s;
            }
            return null;
        }

        /// <summary>
        /// A key that is the typed one with a unit suffix added or dropped - `spacing`
        /// for `spacing_mm`, `curve` for `curve_mm` - is a near miss even when the edit
        /// distance says otherwise.
        /// </summary>
        private static bool Related(string typed, string known)
        {
            string a = (typed ?? "").ToLowerInvariant(), b = (known ?? "").ToLowerInvariant();
            if (a.Length < 3 || b.Length < 3) return false;
            return b.StartsWith(a + "_", StringComparison.Ordinal) || a.StartsWith(b + "_", StringComparison.Ordinal);
        }

        /// <summary>Levenshtein distance, case-insensitive, for the did-you-mean list only.</summary>
        internal static int EditDistance(string a, string b)
        {
            a = (a ?? "").ToLowerInvariant();
            b = (b ?? "").ToLowerInvariant();
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }

        /// <summary>A number that really is one, or a refusal naming where it was.</summary>
        private static double? ReadNumber(JToken t, string at, out string error)
        {
            error = null;
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
            {
                error = at + " is " + t.ToString(Newtonsoft.Json.Formatting.None) + ", which is not a number. " +
                        "It is refused rather than converted: a string throws, and a boolean becomes 1.";
                return null;
            }
            double v = t.Value<double>();
            if (!RebarLayoutRules.IsFinite(v)) { error = at + " is not a finite number."; return null; }
            return v;
        }

        /// <summary>A whole number that really is one. 2.6 is refused, not rounded to 3.</summary>
        private static int? ReadInt(JToken t, string at, out string error)
        {
            error = null;
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer)
            {
                error = at + " is " + t.ToString(Newtonsoft.Json.Formatting.None) + ", which is not a whole " +
                        "number. It is refused rather than rounded: 2.6 bars is not 3 bars, it is a mistake.";
                return null;
            }
            long v = t.Value<long>();
            if (v < int.MinValue || v > int.MaxValue) { error = at + " is outside the range of a bar count."; return null; }
            return (int)v;
        }

        private static double ReadNonNegative(JToken t, double fallback)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) return double.NaN;
            double v = t.Value<double>();
            if (!RebarLayoutRules.IsFinite(v) || v < 0) return double.NaN;
            return v;
        }

        /// <summary>A [x, y, z] direction that is finite and not the zero vector.</summary>
        private static string ReadDirection(JArray a, string at, out double[] v)
        {
            v = null;
            if (a == null)
                return at + " is required: the direction the zones run in, as [x, y, z]. The same profile " +
                       "distributes along a beam or up a column depending on it, and the geometry does not say " +
                       "which.";
            if (a.Count != 3) return at + " must have three numbers.";
            var d = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (a[i].Type != JTokenType.Integer && a[i].Type != JTokenType.Float)
                    return at + " carries a value that is not a number.";
                d[i] = a[i].Value<double>();
                if (!RebarLayoutRules.IsFinite(d[i])) return at + " carries a value that is not finite.";
            }
            if (Math.Abs(d[0]) + Math.Abs(d[1]) + Math.Abs(d[2]) < 1e-9) return at + " is the zero vector.";
            v = d;
            return null;
        }

        /// <summary>A list of [x, y, z] points, all finite.</summary>
        private static string ReadPointList(JArray a, string at, List<double[]> into)
        {
            if (a == null) return at + " is required.";
            foreach (JToken t in a)
            {
                var p = t as JArray;
                if (p == null || p.Count != 3) return at + " must be a list of [x, y, z] points.";
                var v = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (p[i].Type != JTokenType.Integer && p[i].Type != JTokenType.Float)
                        return at + " carries a value that is not a number.";
                    v[i] = p[i].Value<double>();
                    if (!RebarLayoutRules.IsFinite(v[i])) return at + " carries a value that is not finite.";
                }
                into.Add(v);
            }
            return null;
        }

        // ------------------------------------------------------------ pieces

        private static string ReadSelector(JObject o, StructuralHostSelector sel, string where)
        {
            if (o == null) return where + " is required: a rule must say WHICH elements it applies to.";
            sel.Category = o.Value<string>("category");
            sel.TypeName = o.Value<string>("type_name");
            foreach (JToken t in o["element_ids"] as JArray ?? new JArray())
            {
                // A WHOLE NUMBER, and checked as one. Value<long?>() ROUNDS: 1.5
                // arrived as element 2, which is a different element, silently.
                if (t.Type != JTokenType.Integer)
                    return where + ".element_ids carries " + t.ToString(Newtonsoft.Json.Formatting.None) +
                           ", which is not a whole number. An element id is an integer, and rounding one " +
                           "silently names a different element.";
                long? v = t.Value<long?>();
                if (!v.HasValue) return where + ".element_ids carries something that is not an integer.";
                sel.ElementIds.Add(v.Value);
            }
            if (!sel.Any)
                return where + " selects nothing. Give a category, a type_name or element_ids - an empty selector " +
                       "would match every element in the model, which is never what a reinforcement rule means.";
            return null;
        }

        /// <summary>
        /// ABSENT AND UNUSABLE ARE DIFFERENT REFUSALS, and they were once the same
        /// one: a rule with no curve_mm at all came back as geometry_not_usable,
        /// which sends a caller looking at coordinates that were never sent. The
        /// code says which, and the message says what to do about it.
        /// </summary>
        private static string ReadCurves(JObject o, StructuralRebarRule r, out string code)
        {
            code = CodeGeometry;
            JArray curves = o["curve_mm"] as JArray;
            if (curves == null)
            {
                code = CodeMissing;
                return "reinforcement_rules['" + r.Id + "'] needs curve_mm: the bar centreline as a list of " +
                       "[x, y, z] points in millimetres, model coordinates. This bridge does not derive a bar " +
                       "from a host - where the steel goes inside a member is a design decision.";
            }
            foreach (JToken t in curves)
            {
                var p = t as JArray;
                if (p == null || p.Count != 3)
                    return "reinforcement_rules['" + r.Id + "'].curve_mm must be a list of [x, y, z] triples.";
                // NUMBERS, and finite ones. Value<double>() THROWS on a string, out
                // of a method whose contract is to return a refusal rather than
                // throw; and it happily returns NaN for the token NaN, which then
                // passes every guard downstream.
                var pt = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    if (p[i].Type != JTokenType.Integer && p[i].Type != JTokenType.Float)
                        return "reinforcement_rules['" + r.Id + "'].curve_mm carries " +
                               p[i].ToString(Newtonsoft.Json.Formatting.None) + ", which is not a number.";
                    pt[i] = p[i].Value<double>();
                    if (!RebarLayoutRules.IsFinite(pt[i]))
                        return "reinforcement_rules['" + r.Id + "'].curve_mm carries a value that is not finite.";
                }
                r.CurvesMm.Add(pt);
            }
            r.Closed = o.Value<bool?>("closed") ?? false;
            if (r.CurvesMm.Count < 2)
                return "reinforcement_rules['" + r.Id + "'].curve_mm needs at least two points.";
            if (r.Closed && r.CurvesMm.Count < 3)
                return "reinforcement_rules['" + r.Id + "'] is closed and has fewer than three points, which " +
                       "encloses nothing.";
            // A ZERO-LENGTH SEGMENT is a degenerate curve Revit will refuse deep
            // inside its own geometry engine, with a message about nothing in
            // particular. Refusing here names the point.
            for (int i = 1; i < r.CurvesMm.Count; i++)
                if (Distance(r.CurvesMm[i - 1], r.CurvesMm[i]) < 1e-6)
                    return "reinforcement_rules['" + r.Id + "'].curve_mm has two identical consecutive points at " +
                           "index " + (i - 1) + " and " + i + ", which is a segment of zero length.";
            if (r.Closed && Distance(r.CurvesMm[r.CurvesMm.Count - 1], r.CurvesMm[0]) < 1e-6)
                return "reinforcement_rules['" + r.Id + "'] is closed and also repeats its first point at the " +
                       "end. Declare the corners once; closed adds the last segment.";
            return null;
        }

        /// <summary>
        /// Read a layout block, and by default resolve it as a check.
        ///
        /// A ZONE's layout is not resolvable here. maximum_spacing needs an array
        /// length, and a zone's array length is the ZONE's length - which the zone
        /// planner works out, and which for the remainder zone is not known until
        /// every other zone has been measured. So a zone reads its layout with
        /// `resolveNow` false: the vocabulary is still checked here, and the
        /// arithmetic is checked by StirrupZoneRules.Plan against the real length,
        /// which is the only place it can be checked truthfully.
        /// </summary>
        private static string ReadLayout(JObject o, StructuralRebarRule r, StructuralRequirementSet s,
                                         bool resolveNow = true, string at = null)
        {
            at = at ?? "reinforcement_rules['" + r.Id + "']";
            if (o == null)
                return at + " needs a layout block.";
            r.Layout.Layout = (o.Value<string>("rule") ?? "").Trim();

            // Value<int?>() and Value<double?>() are not readers, they are
            // CONVERTERS. A string throws FormatException out of a method whose
            // contract is to return a refusal rather than throw; a boolean true
            // becomes 1, so spacing_mm: true is a one-millimetre pitch and 901
            // stirrups; and 2.6 ROUNDS to 3, which is a different number of bars.
            // ReadSelector refuses exactly this three hundred lines above, by name.
            string nerr;
            r.Layout.Number = ReadInt(o["number"], at + ".layout.number", out nerr);
            if (nerr != null) return nerr;
            r.Layout.SpacingMm = ReadNumber(o["spacing_mm"], at + ".layout.spacing_mm", out nerr);
            if (nerr != null) return nerr;
            r.Layout.ArrayLengthMm = ReadNumber(o["array_length_mm"], at + ".layout.array_length_mm", out nerr);
            if (nerr != null) return nerr;
            // The ONLY defaults in this file, and they are Revit's own. They are
            // echoed into every plan so they are visible rather than assumed.
            r.Layout.IncludeFirstBar = o.Value<bool?>("include_first_bar") ?? true;
            r.Layout.IncludeLastBar = o.Value<bool?>("include_last_bar") ?? true;
            r.BarsOnNormalSide = o.Value<bool?>("bars_on_normal_side") ?? true;
            StructuralBarTypeRef bt;
            if (s.BarTypes.TryGetValue(r.BarTypeId, out bt)) r.Layout.BarDiameterMm = bt.NominalDiameterMm;

            // THE DIAMETER IS THE MODEL'S TO SUPPLY, and nominal_diameter_mm is an
            // optional convenience. Probing with the declared one and refusing when
            // it is absent made minimum_clear_spacing unloadable unless the set
            // repeated a number the bar type already carries - and the resolver then
            // recomputes the whole layout with the MODEL diameter read from Revit
            // anyway, which is the one Revit counts with.
            //
            // So the probe here validates everything EXCEPT the count, using a
            // stand-in diameter when none was declared. The real count is resolved
            // against the model.
            RebarLayoutRequest probeRequest = r.Layout;
            if (r.Layout.Layout == RebarLayout.MinimumClearSpacing && !r.Layout.BarDiameterMm.HasValue)
            {
                probeRequest = new RebarLayoutRequest
                {
                    Layout = r.Layout.Layout,
                    Number = r.Layout.Number,
                    SpacingMm = r.Layout.SpacingMm,
                    ArrayLengthMm = r.Layout.ArrayLengthMm,
                    IncludeFirstBar = r.Layout.IncludeFirstBar,
                    IncludeLastBar = r.Layout.IncludeLastBar,
                    BarDiameterMm = 1.0
                };
            }
            if (!resolveNow)
            {
                // The one thing that IS checkable without a length: the word.
                if (!RebarLayout.IsKnown(r.Layout.Layout))
                    return at + ".layout.rule must be one of " + string.Join(", ", RebarLayout.All) +
                           " - got " + Show(r.Layout.Layout) + ".";
                return null;
            }

            RebarLayoutPlan probe = RebarLayoutRules.Resolve(probeRequest);
            if (!probe.Ok)
                return at + ".layout: " + probe.Error + " (" + probe.Code + ")";
            return null;
        }

        private static string ReadTermination(JObject o, StructuralTermination t, StructuralRequirementSet s,
                                              string ruleId, string which)
        {
            if (o == null) return null;   // no block means no hook, which is a straight end
            t.HookTypeId = o.Value<string>("hook_type");
            if (t.HookTypeId != null && !s.HookTypes.ContainsKey(t.HookTypeId))
                return "reinforcement_rules['" + ruleId + "']." + which + ".hook_type is '" + t.HookTypeId +
                       "', which no hook_types entry declares.";
            string orientation = o.Value<string>("orientation");
            if (orientation != null)
            {
                if (!RebarApiVocabulary.IsKnown(orientation))
                    return "reinforcement_rules['" + ruleId + "']." + which + ".orientation must be " +
                           string.Join(" or ", RebarApiVocabulary.All) + " - got " + Show(orientation) + ".";
                t.Orientation = orientation;
            }
            return null;
        }

        private static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static StructuralRequirementSet Fail(StructuralRequirementSet s, string code, string message)
        {
            return Fail(s, code, message, null);
        }

        private static StructuralRequirementSet Fail(StructuralRequirementSet s, string code, string message,
                                                     string path)
        {
            s.Code = code;
            s.Error = message;
            s.Path = path;
            // A REFUSED SET CARRIES NO RULES. A caller that reads RebarRules without
            // checking Ok must not find a plausible half of somebody's reinforcement.
            s.CoverRules.Clear();
            s.StirrupZoneRules.Clear();
            s.MatRules.Clear();
            s.RebarRules.Clear();
            return s;
        }

        private static string Show(string s)
        {
            return s == null ? "null" : "'" + s + "'";
        }

        /// <summary>
        /// The same document with every object's properties in ordinal order.
        /// ARRAYS ARE LEFT ALONE: the order of rules and of curve points is part of
        /// what the set says, and sorting them would make two different documents
        /// hash the same.
        /// </summary>
        private static JToken Canonical(JToken t)
        {
            var o = t as JObject;
            if (o != null)
            {
                var sorted = new JObject();
                foreach (JProperty prop in o.Properties().OrderBy(x => x.Name, StringComparer.Ordinal))
                    sorted[prop.Name] = Canonical(prop.Value);
                return sorted;
            }
            var a = t as JArray;
            if (a != null)
            {
                var copy = new JArray();
                foreach (JToken item in a) copy.Add(Canonical(item));
                return copy;
            }
            return t;
        }

        /// <summary>
        /// A stable digest of the whole artefact, for provenance and for the audit.
        ///
        /// CANONICAL, because the digest decides whether a bar is reported as built
        /// from a DIFFERENT version of the set. Hashing the document as written made
        /// that a function of property ORDER: reordering two keys in an editor, with
        /// no change to a single number, marked every bar in the model stale.
        /// </summary>
        public static string Sha256Of(JObject doc)
        {
            string canonical = doc == null ? "" : Canonical(doc).ToString(Newtonsoft.Json.Formatting.None);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
                var sb = new System.Text.StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
