// -----------------------------------------------------------------------------
// Horizun Revit MCP - materials and their appearance. Original Horizun code.
//
// G12 of the 2026-09-14 competitive inventory. Re-verified against 1.3.3 and the
// gap was real and total: neither Material.Create nor AppearanceAssetElement
// appeared anywhere in the source. horizun_manage_system_types builds compound
// structures whose layers REFERENCE materials; nothing could make one, rename
// one, colour one, or give one an appearance.
//
// THE THREE PLACES A MATERIAL LIVES, and why this command is careful about which
// one it is writing:
//
//   GRAPHICS - the colour, the patterns and the transparency Revit draws in a
//   shaded view and on a sheet. Written on the Material itself, always available.
//
//   APPEARANCE - the rendering asset. A material POINTS at an
//   AppearanceAssetElement, and several materials commonly point at the SAME one.
//   Editing that asset in place therefore changes every material sharing it,
//   which is the "change outside the scope" this gap explicitly warns about, so
//   assigning an appearance here DUPLICATES the asset by default and points only
//   this material at the copy. Sharing is available, and it has to be asked for.
//
//   IDENTITY - class, category, and the identity parameters a schedule reads.
//
// WHAT IS NOT HERE. The contents of a rendering asset - its textures, its bitmap
// paths, its procedural parameters - are edited through the Visual Materials API
// (AssetEditScope), which is a different and much larger surface, and one whose
// failure modes are silent. This command duplicates and assigns assets; it does
// not claim to author them, and the reply says so rather than leaving a caller to
// assume the texture changed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ManageMaterialsCommand : ICommand
    {
        public string Name => "horizun_manage_materials";

        public string Description =>
            "Create, duplicate and edit materials - graphics, appearance asset and identity - in one verified batch.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            JArray raw = request["actions"] as JArray;
            if (raw == null || raw.Count < 1 || raw.Count > 200)
                return CommandResult.Fail("actions must contain 1..200 entries.");

            string error;
            List<Plan> plans = PlanAll(doc, raw, out error);
            if (plans == null) return CommandResult.Fail(error + " Nothing was written.");

            string hash = DocumentGate.PlanHash(request, "actions");
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            ResolvedPlan resolved = Resolved(gate, app, plans);

            if (dry)
            {
                DocumentGate.RecordResolvedPlan(resolved);
                var preview = new JObject
                {
                    ["dry_run"] = true,
                    ["valid"] = plans.Count,
                    ["plan"] = new JArray(plans.Select(p => p.Json())),
                    ["shared_appearance_warning"] = SharedAssetWarning(doc, plans)
                };
                ApplicationOutcome.StampRehearsal(preview, plans.Count, 0, 0, 0);
                DocumentGate.StampConfirmation(preview, gate, Name, hash, true,
                    "the token binds each material's unique id and its current graphics, appearance asset and " +
                    "identity, so a material edited by somebody else between the rehearsal and the apply " +
                    "refuses as a stale plan.");
                return CommandResult.Ok(preview);
            }

            DocumentGate.RecordResolvedPlan(resolved);
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name") ?? "Horizun: manage materials";
            using (var group = new TransactionGroup(doc, txName))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Could not start the material TransactionGroup.");
                var tx = new Transaction(doc, txName);
                TransactionStatus txStatus = TransactionStatus.Uninitialized;
                try
                {
                    if (tx.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Could not start the material transaction.");
                    foreach (Plan p in plans) Apply(doc, p);
                    doc.Regenerate();
                    string why;
                    if (!plans.All(p => Verify(doc, p, out why)))
                        throw new InvalidOperationException(
                            "A material postcondition failed while the batch was still reversible.");
                    txStatus = tx.Commit();
                    if (txStatus != TransactionStatus.Committed)
                        throw new InvalidOperationException("The material transaction returned " + txStatus + ".");
                    if (!plans.All(p => Verify(doc, p, out why)))
                        throw new InvalidOperationException(
                            "A material postcondition failed after the transaction committed.");
                    TransactionStatus assimilated = group.Assimilate();
                    if (assimilated != TransactionStatus.Committed)
                        return CommandResult.FailWithDetail(
                            "The material group did not assimilate; state is uncertain.",
                            new JObject
                            {
                                ["state"] = "uncertain",
                                ["transaction_group_status"] = assimilated.ToString()
                            });
                }
                catch (Exception ex)
                {
                    Guard.RollbackResult? txRollback = null;
                    try { if (tx.GetStatus() == TransactionStatus.Started) txRollback = Guard.RollBack(tx); } catch { }
                    Guard.RollbackResult? groupRollback;
                    try { groupRollback = Guard.RollBack(group); } catch { groupRollback = null; }
                    return CommandResult.FailWithDetail(
                        "The material batch failed and was rolled back: " + ex.Message,
                        new JObject
                        {
                            ["state"] = groupRollback.HasValue && groupRollback.Value.Confirmed
                                ? "rolled_back" : "uncertain",
                            ["transaction_status"] = txRollback.HasValue
                                ? txRollback.Value.StatusName : txStatus.ToString(),
                            ["transaction_group_status"] = groupRollback.HasValue
                                ? groupRollback.Value.StatusName : "Error"
                        });
                }
            }

            var rows = new JArray();
            foreach (Plan p in plans)
            {
                string why;
                bool ok = Verify(doc, p, out why);
                JObject row = p.Result(doc);
                row["verified"] = ok;
                row["not_verified_because"] = ok ? (JToken)JValue.CreateNull() : why;

                // PER FIELD, on both paths. A material whose density landed and whose
                // Poisson ratio was refused is not "written" and not "failed": it is those
                // two facts, and a single flag cannot carry them.
                if (p.StructuralOutcomes.Count > 0 || p.StructuralRefusal != null)
                    row["structural_fields"] = new JObject
                    {
                        ["refusal"] = p.StructuralRefusal == null
                            ? (JToken)JValue.CreateNull() : p.StructuralRefusal,
                        ["fields"] = new JArray(p.StructuralOutcomes.Select(o => (JToken)o.Json()))
                    };
                if (p.ThermalOutcomes.Count > 0 || p.ThermalRefusal != null)
                    row["thermal_fields"] = new JObject
                    {
                        ["refusal"] = p.ThermalRefusal == null
                            ? (JToken)JValue.CreateNull() : p.ThermalRefusal,
                        ["fields"] = new JArray(p.ThermalOutcomes.Select(o => (JToken)o.Json()))
                    };

                // AND WHAT THE MATERIAL NOW HOLDS, read back from the assets rather than
                // echoed from the request.
                Material written = p.Target ?? p.Source;
                if (written != null) row["assets"] = MaterialAssets.Read(doc, written);

                rows.Add(row);
            }

            var done = new JObject
            {
                ["state"] = "committed_verified",
                ["host_verified"] = true,
                ["rows"] = rows
            };
            ApplicationOutcome.StampApplied(done, ApplicationOutcome.Committed,
                                            plans.Count, plans.Count, plans.Count, 0, 0, 0);
            return CommandResult.Ok(done);
        }

        // =====================================================================
        // Planning
        // =====================================================================

        private static List<Plan> PlanAll(Document doc, JArray raw, out string error)
        {
            error = null;
            var plans = new List<Plan>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var claimedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < raw.Count; i++)
            {
                JObject a = raw[i] as JObject;
                if (a == null) { error = "actions[" + i + "] is not an object."; return null; }

                string key = a.Value<string>("key");
                if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                { error = "actions[" + i + "].key is empty or duplicated."; return null; }

                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
                if (op != "create" && op != "duplicate" && op != "update")
                { error = "actions[" + i + "].operation must be create, duplicate or update."; return null; }

                var plan = new Plan { Index = i, Key = key, Operation = op, Input = a };

                if (op == "create" || op == "duplicate")
                {
                    string name = a.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name))
                    { error = "actions[" + i + "].name is required by " + op + "."; return null; }
                    if (!claimedNames.Add(name))
                    { error = "actions[" + i + "]: name '" + name + "' appears twice in this batch."; return null; }
                    if (Existing(doc, name) != null)
                    {
                        error = "actions[" + i + "]: a material named '" + name + "' already exists (id " +
                                Rid.Value(Existing(doc, name).Id) + "). Material names are unique in a Revit " +
                                "document; use update to change that one.";
                        return null;
                    }
                    plan.NewName = name;
                }

                if (op == "duplicate" || op == "update")
                {
                    long id = a.Value<long?>("material_id") ?? -1;
                    Material source = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as Material : null;
                    if (source == null && a["material_name"] != null)
                        source = Existing(doc, a.Value<string>("material_name"));
                    if (source == null)
                    {
                        error = "actions[" + i + "]: material_id or material_name must identify an existing " +
                                "material for " + op + ".";
                        return null;
                    }
                    plan.Source = source;
                    plan.Before = Snapshot(doc, source);
                }

                string colourError = CheckColours(a);
                if (colourError != null) { error = "actions[" + i + "]: " + colourError; return null; }

                int? transparency = a.Value<int?>("transparency");
                if (transparency != null && (transparency < 0 || transparency > 100))
                { error = "actions[" + i + "]: transparency must be 0..100."; return null; }

                int? shininess = a.Value<int?>("shininess");
                if (shininess != null && (shininess < 0 || shininess > 128))
                { error = "actions[" + i + "]: shininess must be 0..128, which is Revit's whole range."; return null; }

                if (a["appearance_asset_id"] != null)
                {
                    long assetId = a.Value<long?>("appearance_asset_id") ?? -1;
                    var asset = Rid.CanRepresent(assetId)
                        ? doc.GetElement(Rid.Make(assetId)) as AppearanceAssetElement : null;
                    if (asset == null)
                    { error = "actions[" + i + "]: appearance_asset_id does not identify an appearance asset."; return null; }
                    plan.Asset = asset;
                }

                if (op == "update" && !Changes(a))
                {
                    error = "actions[" + i + "] changes nothing: pass at least one of name, colour, patterns, " +
                            "transparency, shininess, smoothness, class, category or appearance_asset_id.";
                    return null;
                }

                plans.Add(plan);
            }
            return plans;
        }

        private static bool Changes(JObject a)
        {
            foreach (string field in new[]
                     {
                         "name", "color", "surface_pattern_color", "cut_pattern_color",
                         "surface_pattern", "cut_pattern", "transparency", "shininess", "smoothness",
                         "material_class", "material_category", "appearance_asset_id"
                     })
                if (a[field] != null) return true;
            return false;
        }

        private static string CheckColours(JObject a)
        {
            foreach (string field in new[] { "color", "surface_pattern_color", "cut_pattern_color" })
            {
                string text = a.Value<string>(field);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (ParseColour(text) == null)
                    return field + " must be a six-digit hex colour like #B0B0B0; '" + text + "' is not one.";
            }
            return null;
        }

        private static Color ParseColour(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string hex = text.Trim().TrimStart('#');
            if (hex.Length != 6) return null;
            try
            {
                return new Color(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { return null; }
        }

        private static Material Existing(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (Material m in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
            {
                string existing;
                try { existing = m.Name; } catch { continue; }
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) return m;
            }
            return null;
        }

        // =====================================================================
        // Apply
        // =====================================================================

        private static void Apply(Document doc, Plan p)
        {
            Material material;
            switch (p.Operation)
            {
                case "create":
                    material = doc.GetElement(Material.Create(doc, p.NewName)) as Material;
                    break;
                case "duplicate":
                    material = p.Source.Duplicate(p.NewName);
                    break;
                default:
                    material = p.Source;
                    string rename = p.Input.Value<string>("name");
                    if (!string.IsNullOrWhiteSpace(rename) && material.Name != rename) material.Name = rename;
                    break;
            }
            if (material == null)
                throw new InvalidOperationException("the material could not be created or read back");
            p.Target = material;

            Color colour = ParseColour(p.Input.Value<string>("color"));
            if (colour != null) material.Color = colour;

            Color surfaceColour = ParseColour(p.Input.Value<string>("surface_pattern_color"));
            if (surfaceColour != null) material.SurfaceForegroundPatternColor = surfaceColour;

            // ---- physical and thermal properties ---------------------------------
            // INSIDE THIS TRANSACTION, like every other field. The shared-asset question is
            // asked by MaterialAssets before it writes anything: a caller who did not ask
            // for a duplicate and whose asset is shared gets a refusal naming the other
            // materials, and nothing is written for that material.
            bool duplicateShared = p.Input.Value<bool?>("duplicate_shared_assets") ?? false;

            var structural = p.Input["structural"] as JObject;
            if (structural != null)
            {
                string refusal;
                p.StructuralOutcomes.AddRange(
                    MaterialAssets.WriteStructural(doc, material, structural, duplicateShared, out refusal));
                p.StructuralRefusal = refusal;
            }

            var thermal = p.Input["thermal"] as JObject;
            if (thermal != null)
            {
                string refusal;
                p.ThermalOutcomes.AddRange(
                    MaterialAssets.WriteThermal(doc, material, thermal, duplicateShared, out refusal));
                p.ThermalRefusal = refusal;
            }

            Color cutColour = ParseColour(p.Input.Value<string>("cut_pattern_color"));
            if (cutColour != null) material.CutForegroundPatternColor = cutColour;

            string surfacePattern = p.Input.Value<string>("surface_pattern");
            if (!string.IsNullOrWhiteSpace(surfacePattern))
                material.SurfaceForegroundPatternId = PatternId(doc, surfacePattern, FillPatternTarget.Drafting);

            string cutPattern = p.Input.Value<string>("cut_pattern");
            if (!string.IsNullOrWhiteSpace(cutPattern))
                material.CutForegroundPatternId = PatternId(doc, cutPattern, FillPatternTarget.Drafting);

            int? transparency = p.Input.Value<int?>("transparency");
            if (transparency != null) material.Transparency = transparency.Value;

            int? shininess = p.Input.Value<int?>("shininess");
            if (shininess != null) material.Shininess = shininess.Value;

            int? smoothness = p.Input.Value<int?>("smoothness");
            if (smoothness != null) material.Smoothness = smoothness.Value;

            string materialClass = p.Input.Value<string>("material_class");
            if (!string.IsNullOrWhiteSpace(materialClass)) material.MaterialClass = materialClass;

            string materialCategory = p.Input.Value<string>("material_category");
            if (!string.IsNullOrWhiteSpace(materialCategory)) material.MaterialCategory = materialCategory;

            if (p.Asset != null)
            {
                // SEVERAL MATERIALS COMMONLY SHARE ONE ASSET. Pointing this material at the
                // caller's asset directly is fine; the danger is the opposite direction -
                // a later edit of that asset changing every material using it. Duplicating
                // by default means this material owns what it points at, and sharing is a
                // thing somebody asked for rather than a thing that happened.
                bool share = p.Input.Value<bool?>("share_appearance_asset") ?? false;
                AppearanceAssetElement asset = p.Asset;
                if (!share)
                {
                    string copyName = UniqueAssetName(doc, material.Name + " - " + p.Key);
                    asset = p.Asset.Duplicate(copyName);
                    p.AssetDuplicated = true;
                }
                material.AppearanceAssetId = asset.Id;
                p.AppliedAssetId = asset.Id;
            }
        }

        private static string UniqueAssetName(Document doc, string wanted)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppearanceAssetElement a in new FilteredElementCollector(doc)
                         .OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>())
            {
                try { taken.Add(a.Name); } catch { }
            }
            if (!taken.Contains(wanted)) return wanted;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = wanted + " " + i;
                if (!taken.Contains(candidate)) return candidate;
            }
            throw new InvalidOperationException("could not find an unused appearance asset name for " + wanted);
        }

        private static ElementId PatternId(Document doc, string name, FillPatternTarget target)
        {
            foreach (FillPatternElement f in new FilteredElementCollector(doc)
                         .OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                FillPattern pattern;
                try { pattern = f.GetFillPattern(); } catch { continue; }
                if (pattern == null || pattern.Target != target) continue;
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f.Id;
                if (string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase) && pattern.IsSolidFill)
                    return f.Id;
            }
            throw new ArgumentException(
                "no fill pattern named '" + name + "' exists for " + target + ". Pass 'solid' for the solid " +
                "fill, or load the pattern first.");
        }

        // =====================================================================
        // Verify - everything re-read from the model
        // =====================================================================

        private static bool Verify(Document doc, Plan p, out string why)
        {
            why = null;
            if (p.Target == null) { why = "the material was never created"; return false; }

            var material = doc.GetElement(p.Target.Id) as Material;
            if (material == null) { why = "the material no longer exists"; return false; }

            string wantedName = p.Operation == "update"
                ? p.Input.Value<string>("name")
                : p.NewName;
            if (!string.IsNullOrWhiteSpace(wantedName) &&
                !string.Equals(material.Name, wantedName, StringComparison.Ordinal))
            { why = "the name did not stick"; return false; }

            Color colour = ParseColour(p.Input.Value<string>("color"));
            if (colour != null && !SameColour(material.Color, colour))
            { why = "the colour did not stick"; return false; }

            int? transparency = p.Input.Value<int?>("transparency");
            if (transparency != null && material.Transparency != transparency.Value)
            { why = "the transparency did not stick"; return false; }

            int? shininess = p.Input.Value<int?>("shininess");
            if (shininess != null && material.Shininess != shininess.Value)
            { why = "the shininess did not stick"; return false; }

            string materialClass = p.Input.Value<string>("material_class");
            if (!string.IsNullOrWhiteSpace(materialClass) &&
                !string.Equals(material.MaterialClass, materialClass, StringComparison.Ordinal))
            { why = "the material class did not stick"; return false; }

            if (p.AppliedAssetId != null &&
                Rid.Value(material.AppearanceAssetId) != Rid.Value(p.AppliedAssetId))
            { why = "the appearance asset did not stick"; return false; }

            // AN ASSET WRITE THAT WAS REFUSED IS NOT A VERIFIED MATERIAL. The refusal is a
            // decision this command made - a shared asset it would not edit - and reporting
            // the material as verified would hide the thing the caller has to act on.
            if (p.StructuralRefusal != null) { why = p.StructuralRefusal; return false; }
            if (p.ThermalRefusal != null) { why = p.ThermalRefusal; return false; }

            AssetFieldOutcome badStructural = p.StructuralOutcomes.FirstOrDefault(
                o => o.State != MaterialAssets.Updated && o.State != MaterialAssets.Unchanged);
            if (badStructural != null)
            { why = "structural field '" + badStructural.Field + "': " + badStructural.Reason; return false; }

            AssetFieldOutcome badThermal = p.ThermalOutcomes.FirstOrDefault(
                o => o.State != MaterialAssets.Updated && o.State != MaterialAssets.Unchanged);
            if (badThermal != null)
            { why = "thermal field '" + badThermal.Field + "': " + badThermal.Reason; return false; }

            return true;
        }

        private static bool SameColour(Color actual, Color wanted)
        {
            if (actual == null || !actual.IsValid) return false;
            return actual.Red == wanted.Red && actual.Green == wanted.Green && actual.Blue == wanted.Blue;
        }

        /// <summary>
        /// Which appearance assets in this batch are shared with materials the batch is
        /// NOT touching. Reported in the dry run, because "I changed one material" and
        /// "I changed the look of eleven" are the same call when an asset is shared.
        /// </summary>
        private static JArray SharedAssetWarning(Document doc, List<Plan> plans)
        {
            var rows = new JArray();
            foreach (Plan p in plans)
            {
                if (p.Asset == null || (p.Input.Value<bool?>("share_appearance_asset") ?? false) == false) continue;
                var users = new List<long>();
                foreach (Material m in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
                {
                    try
                    {
                        if (Rid.Value(m.AppearanceAssetId) == Rid.Value(p.Asset.Id)) users.Add(Rid.Value(m.Id));
                    }
                    catch { }
                }
                if (users.Count <= 1) continue;
                rows.Add(new JObject
                {
                    ["key"] = p.Key,
                    ["appearance_asset_id"] = Rid.Value(p.Asset.Id),
                    ["already_used_by"] = new JArray(users),
                    ["means"] = "share_appearance_asset was requested and this asset is already used by " +
                                users.Count + " materials. Editing it later changes all of them."
                });
            }
            return rows;
        }

        // =====================================================================
        // Snapshot and plan bookkeeping
        // =====================================================================

        private static string Snapshot(Document doc, Material m)
        {
            try
            {
                Color c = m.Color;
                return string.Join("|", new[]
                {
                    m.Name,
                    c != null && c.IsValid ? c.Red + "," + c.Green + "," + c.Blue : "novalue",
                    m.Transparency.ToString(),
                    m.Shininess.ToString(),
                    m.MaterialClass ?? "",
                    m.MaterialCategory ?? "",
                    Rid.Value(m.AppearanceAssetId).ToString()
                });
            }
            catch { return "<unreadable>"; }
        }

        private static ResolvedPlan Resolved(GateResult gate, UIApplication app, List<Plan> plans)
        {
            var resolved = new ResolvedPlan
            {
                Command = "horizun_manage_materials",
                DocumentKey = gate.Fingerprint,
                RevitVersion = app.Application.VersionNumber,
                DocumentFingerprint = gate.Identity.FingerprintDigest()
            };
            foreach (Plan p in plans)
            {
                var before = new Dictionary<string, string>
                {
                    ["operation"] = p.Operation,
                    ["source"] = p.Source == null ? "<new>" : SafeUniqueId(p.Source),
                    ["before"] = p.Before ?? "<new>",
                    ["new_name"] = p.NewName ?? ""
                };
                resolved.Elements.Add(new PlannedElement
                {
                    UniqueId = "material:" + p.Key,
                    Category = "material",
                    Action = p.Operation == "update" ? PlannedAction.Modify : PlannedAction.Create,
                    BeforeValues = before
                });
            }
            return resolved;
        }

        private static string SafeUniqueId(Element element)
        {
            try { return element.UniqueId; } catch { return "<unreadable>"; }
        }

        private sealed class Plan
        {
            public int Index;
            public string Key;
            public string Operation;
            public JObject Input;
            public string NewName;
            public string Before;
            public Material Source;
            public Material Target;
            public AppearanceAssetElement Asset;
            public ElementId AppliedAssetId;
            public bool AssetDuplicated;

            /// <summary>Per-field outcomes of the structural and thermal writes, or empty.</summary>
            public readonly List<AssetFieldOutcome> StructuralOutcomes = new List<AssetFieldOutcome>();
            public readonly List<AssetFieldOutcome> ThermalOutcomes = new List<AssetFieldOutcome>();

            /// <summary>Why an asset write did not happen at all, or null.</summary>
            public string StructuralRefusal;
            public string ThermalRefusal;

            public JObject Json() => new JObject
            {
                ["index"] = Index,
                ["key"] = Key,
                ["operation"] = Operation,
                ["material_id"] = Source == null ? (JToken)JValue.CreateNull() : Rid.Value(Source.Id),
                ["name"] = NewName ?? Input.Value<string>("name"),
                ["appearance_asset_id"] = Asset == null ? (JToken)JValue.CreateNull() : Rid.Value(Asset.Id)
            };

            public JObject Result(Document doc)
            {
                JObject row = Json();
                row["created_material_id"] = Target == null ? (JToken)JValue.CreateNull() : Rid.Value(Target.Id);
                row["appearance_asset_duplicated"] = AssetDuplicated;
                row["applied_appearance_asset_id"] = AppliedAssetId == null
                    ? (JToken)JValue.CreateNull() : Rid.Value(AppliedAssetId);
                row["means"] = "every value above was re-read from the material after the commit. The CONTENTS " +
                               "of a rendering asset - its textures and procedural parameters - are not edited " +
                               "by this command and are not reported as though they were.";
                return row;
            }
        }
    }
}
