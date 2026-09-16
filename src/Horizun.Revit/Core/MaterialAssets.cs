// -----------------------------------------------------------------------------
// Horizun Revit MCP - the physical and thermal properties of a material.
// Original Horizun code.
//
// G12 LEFT THESE OUT and the backlog called them pending. They are not blocked:
// the whole API is present in 2023-2027, read from the documentation Autodesk
// ships beside each RevitAPI.dll -
//
//   Material.StructuralAssetId, Material.ThermalAssetId        (properties)
//   PropertySetElement.GetStructuralAsset / SetStructuralAsset
//   PropertySetElement.GetThermalAsset   / SetThermalAsset
//   PropertySetElement.Create(Document, StructuralAsset|ThermalAsset)
//   StructuralAsset(name, StructuralAssetClass) and ThermalAsset(name, type)
//
// - so "pending" meant unwritten, not impossible.
//
// THE ONE THING THAT MAKES THIS DANGEROUS, and it is the same shape as the
// appearance asset this file's neighbour already guards: A PROPERTY SET IS AN
// ELEMENT, AND SEVERAL MATERIALS POINT AT THE SAME ONE. Revit's own template ships
// "Concrete" assets shared by a dozen materials. Editing the asset of one material
// silently changes every material that shares it - and the caller asked about one.
// So the users are COUNTED before anything is written, and an edit that would
// reach more than the named material is refused unless the caller asked for a
// duplicate. The refusal names the other materials.
//
// TWO RULES ABOUT REPORTING, both of which this file exists to keep:
//
//   A VALUE THAT WAS NOT READ IS NOT ZERO. Every field comes back as a value or as
//   null with a reason. `density: 0` and "this material has no structural asset"
//   are different facts and a reader cannot tell them apart from a number.
//
//   A FIELD THAT IS NOT SUPPORTED IS NOT "WRITTEN". Every write is re-read from
//   the asset afterwards and reported per field, so a name this build does not
//   know is reported as unknown rather than counted among the changes.
//
// UNITS ARE REVIT'S INTERNAL ONES and they are DECLARED rather than converted.
// Density is mass per cubic foot, moduli are force per square foot, and a
// conversion invented here would be a number nobody could trace back to what the
// model holds. The reply says which unit each field is in.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What happened to one asset field.</summary>
    public sealed class AssetFieldOutcome
    {
        public string Field;
        public string State;          // updated | unchanged | unknown_field | refused
        public string Was;
        public string Now;
        public string Reason;

        public JObject Json() => new JObject
        {
            ["field"] = Field,
            ["state"] = State,
            ["was"] = Was == null ? (JToken)JValue.CreateNull() : Was,
            ["now"] = Now == null ? (JToken)JValue.CreateNull() : Now,
            ["reason"] = Reason == null ? (JToken)JValue.CreateNull() : Reason
        };
    }

    public static class MaterialAssets
    {
        /// <summary>
        /// The structural classes this build will create, by name.
        ///
        /// A CLOSED LIST that excludes Undefined and Basic: an asset created as Undefined
        /// accepts almost nothing and reads as a material somebody configured, which is
        /// the worst of both. The class is always the caller's choice; this only bounds
        /// which choices are honoured.
        /// </summary>
        public static readonly string[] StructuralClasses =
            { "Concrete", "Metal", "Wood", "Plastic", "Generic", "Gas", "Liquid" };

        /// <summary>The thermal types this build will create. Undefined is excluded for the same reason.</summary>
        public static readonly string[] ThermalTypes = { "Solid", "Liquid", "Gas" };

        public const string Updated = "updated";
        public const string Unchanged = "unchanged";
        public const string UnknownField = "unknown_field";
        public const string Refused = "refused";

        // =====================================================================
        // Reading
        // =====================================================================

        /// <summary>
        /// The physical and thermal properties of a material, or an explicit absence.
        ///
        /// ABSENCE IS REPORTED, NEVER ZEROED. A material with no structural asset gets
        /// `present: false` and no numbers at all, because a reader handed `density: 0`
        /// cannot tell it from a material whose density is genuinely zero.
        /// </summary>
        public static JObject Read(Document doc, Material material)
        {
            return new JObject
            {
                ["structural"] = ReadStructural(doc, material),
                ["thermal"] = ReadThermal(doc, material),
                ["units"] =
                    "REVIT'S INTERNAL UNITS, declared rather than converted: density is mass per cubic foot, " +
                    "moduli and strengths are force per square foot, thermal conductivity is per foot per " +
                    "degree. A conversion invented here would produce a number nobody could trace back to " +
                    "what the model holds."
            };
        }

        private static JObject ReadStructural(Document doc, Material material)
        {
            var report = new JObject();
            PropertySetElement set = SetOf(doc, material?.StructuralAssetId);
            if (set == null)
            {
                report["present"] = false;
                report["means"] = "this material carries no structural asset. That is not a density of zero: " +
                                  "there is nothing to read.";
                return report;
            }

            StructuralAsset asset;
            try { asset = set.GetStructuralAsset(); }
            catch (Exception ex)
            {
                report["present"] = false;
                report["means"] = "this material names a structural property set that could not be read (" +
                                  ex.Message + "). Nothing is claimed about its values.";
                return report;
            }

            if (asset == null)
            {
                report["present"] = false;
                report["means"] = "the property set exists and holds no structural asset.";
                return report;
            }

            report["present"] = true;
            report["property_set_id"] = Rid.Value(set.Id);
            report["shared_with"] = new JArray(UsersOfStructural(doc, set.Id)
                                               .Where(id => id != Rid.Value(material.Id)));
            report["name"] = Safe(() => asset.Name);
            report["class"] = Safe(() => asset.StructuralAssetClass.ToString());
            report["behaviour"] = Safe(() => asset.Behavior.ToString());
            report["density"] = Number(() => asset.Density);
            report["young_modulus"] = Vector(() => asset.YoungModulus);
            report["poisson_ratio"] = Vector(() => asset.PoissonRatio);
            report["shear_modulus"] = Vector(() => asset.ShearModulus);
            report["thermal_expansion_coefficient"] = Vector(() => asset.ThermalExpansionCoefficient);
            report["minimum_yield_stress"] = Number(() => asset.MinimumYieldStress);
            report["minimum_tensile_strength"] = Number(() => asset.MinimumTensileStrength);
            report["concrete_compression"] = Number(() => asset.ConcreteCompression);
            report["lightweight"] = Safe(() => asset.Lightweight.ToString());
            report["means"] =
                "read from the asset the material points at. A field reported as null could not be read from " +
                "THIS asset class - a concrete asset has no wood grade - and is not a zero.";
            return report;
        }

        private static JObject ReadThermal(Document doc, Material material)
        {
            var report = new JObject();
            PropertySetElement set = SetOf(doc, material?.ThermalAssetId);
            if (set == null)
            {
                report["present"] = false;
                report["means"] = "this material carries no thermal asset. Nothing to read; not a zero.";
                return report;
            }

            ThermalAsset asset;
            try { asset = set.GetThermalAsset(); }
            catch (Exception ex)
            {
                report["present"] = false;
                report["means"] = "this material names a thermal property set that could not be read (" +
                                  ex.Message + ").";
                return report;
            }

            if (asset == null)
            {
                report["present"] = false;
                report["means"] = "the property set exists and holds no thermal asset.";
                return report;
            }

            report["present"] = true;
            report["property_set_id"] = Rid.Value(set.Id);
            report["shared_with"] = new JArray(UsersOfThermal(doc, set.Id)
                                               .Where(id => id != Rid.Value(material.Id)));
            report["name"] = Safe(() => asset.Name);
            report["material_type"] = Safe(() => asset.ThermalMaterialType.ToString());
            report["behaviour"] = Safe(() => asset.Behavior.ToString());
            report["thermal_conductivity"] = Number(() => asset.ThermalConductivity);
            report["specific_heat"] = Number(() => asset.SpecificHeat);
            report["density"] = Number(() => asset.Density);
            report["emissivity"] = Number(() => asset.Emissivity);
            report["permeability"] = Number(() => asset.Permeability);
            report["porosity"] = Number(() => asset.Porosity);
            report["reflectivity"] = Number(() => asset.Reflectivity);
            report["transmits_light"] = Safe(() => asset.TransmitsLight.ToString());
            report["means"] =
                "read from the asset the material points at. A null field could not be read from THIS asset " +
                "type and is not a zero.";
            return report;
        }

        // =====================================================================
        // Writing
        // =====================================================================

        /// <summary>
        /// Who else would be changed by editing this material's structural asset.
        ///
        /// THE QUESTION THAT HAS TO BE ASKED BEFORE WRITING. Revit's own templates ship
        /// assets shared by a dozen materials, and a caller who asked about one material
        /// has not asked to change the other eleven.
        /// </summary>
        public static List<long> UsersOfStructural(Document doc, ElementId setId) =>
            doc == null || setId == null ? new List<long>()
                : new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .Where(m => Rid.Value(m.StructuralAssetId) == Rid.Value(setId))
                    .Select(m => Rid.Value(m.Id)).OrderBy(id => id).ToList();

        public static List<long> UsersOfThermal(Document doc, ElementId setId) =>
            doc == null || setId == null ? new List<long>()
                : new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .Where(m => Rid.Value(m.ThermalAssetId) == Rid.Value(setId))
                    .Select(m => Rid.Value(m.Id)).OrderBy(id => id).ToList();

        /// <summary>
        /// Write structural fields, inside a transaction the caller has open.
        ///
        /// `duplicateWhenShared` is the caller's answer to the question above. False and
        /// shared means REFUSED, with the other materials named: an edit that silently
        /// reaches eleven materials somebody did not mention is the worst outcome here,
        /// and it is invisible afterwards.
        /// </summary>
        public static List<AssetFieldOutcome> WriteStructural(Document doc, Material material,
                                                              JObject fields, bool duplicateWhenShared,
                                                              out string refusal)
        {
            refusal = null;
            var outcomes = new List<AssetFieldOutcome>();
            if (doc == null || material == null || fields == null || !fields.Properties().Any()) return outcomes;

            PropertySetElement set = SetOf(doc, material.StructuralAssetId);
            StructuralAsset asset = null;
            if (set != null) { try { asset = set.GetStructuralAsset(); } catch { } }

            if (asset == null)
            {
                // NO ASSET: CREATE ONE, BUT ONLY WITH A CLASS SOMEBODY CHOSE. The class
                // decides which fields exist - a concrete asset has no yield stress and a
                // metal one has no concrete compression - so choosing it here would produce
                // a material whose properties nobody picked, in somebody else's model.
                string wanted = fields.Value<string>("class");
                if (string.IsNullOrWhiteSpace(wanted))
                {
                    refusal = "this material has no structural asset, and creating one needs a CLASS: it " +
                              "decides which fields exist at all. Send it as structural.class, one of: " +
                              string.Join(", ", StructuralClasses) + ". Nothing was written, and no class " +
                              "was chosen for you.";
                    return outcomes;
                }

                StructuralAssetClass assetClass;
                if (!Enum.TryParse(wanted, true, out assetClass) ||
                    !StructuralClasses.Contains(assetClass.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    refusal = "'" + wanted + "' is not a structural asset class this build accepts. The " +
                              "supported ones are: " + string.Join(", ", StructuralClasses) + ".";
                    return outcomes;
                }

                try
                {
                    string name = fields.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name)) name = material.Name + " structural";
                    var created = new StructuralAsset(name, assetClass);
                    PropertySetElement element = PropertySetElement.Create(doc, created);
                    material.StructuralAssetId = element.Id;
                    set = element;
                    asset = element.GetStructuralAsset();
                    outcomes.Add(new AssetFieldOutcome
                    {
                        Field = "(asset)",
                        State = Updated,
                        Now = "property set " + Rid.Value(element.Id) + ", class " + assetClass,
                        Reason = "created because this material had none, with the class this call named. " +
                                 "Which fields it accepts follows from that class."
                    });
                }
                catch (Exception ex)
                {
                    refusal = "the structural asset could not be created (" + ex.Message + ").";
                    return outcomes;
                }
            }

            List<long> others = UsersOfStructural(doc, set.Id)
                .Where(id => id != Rid.Value(material.Id)).ToList();
            if (others.Count > 0 && !duplicateWhenShared)
            {
                refusal = "this structural asset is shared with " + others.Count + " other material(s) (" +
                          string.Join(", ", others.Take(20)) + (others.Count > 20 ? ", …" : "") +
                          "). Editing it changes every one of them. Nothing was written. Send " +
                          "duplicate_shared_assets=true to give THIS material its own copy first, or edit " +
                          "the ones you mean.";
                return outcomes;
            }

            // THE DUPLICATE, when asked for. A new PropertySetElement carrying a copy of the
            // asset, pointed at by this material alone - so the write below reaches nothing
            // else.
            if (others.Count > 0)
            {
                try
                {
                    PropertySetElement copy = PropertySetElement.Create(doc, asset);
                    material.StructuralAssetId = copy.Id;
                    set = copy;
                    asset = copy.GetStructuralAsset();
                    outcomes.Add(new AssetFieldOutcome
                    {
                        Field = "(asset)",
                        State = Updated,
                        Now = "property set " + Rid.Value(copy.Id),
                        Reason = "this material now has its OWN structural asset, copied from the shared one. " +
                                 "The " + others.Count + " material(s) that shared it are untouched."
                    });
                }
                catch (Exception ex)
                {
                    refusal = "the shared structural asset could not be duplicated (" + ex.Message +
                              "), so nothing was written: editing the shared one would have changed " +
                              others.Count + " other material(s).";
                    return outcomes;
                }
            }

            foreach (JProperty field in fields.Properties())
                outcomes.Add(SetStructuralField(asset, field));

            // WRITTEN BACK AS A WHOLE, because StructuralAsset is a value object: changing
            // the instance in hand does nothing until the property set takes it.
            try { set.SetStructuralAsset(asset); }
            catch (Exception ex)
            {
                refusal = "the edited structural asset was refused by Revit (" + ex.Message + "). Nothing " +
                          "this call changed is in the model.";
                return outcomes;
            }

            // RE-READ. Assigning a property on a value object and calling the setter is not
            // evidence that the value survived validation.
            StructuralAsset after = null;
            try { after = set.GetStructuralAsset(); } catch { }
            foreach (AssetFieldOutcome outcome in outcomes)
                if (outcome.State == Updated && outcome.Field != "(asset)")
                    outcome.Now = ReadStructuralField(after, outcome.Field);

            return outcomes;
        }

        /// <summary>Write thermal fields. Same rules, same refusals.</summary>
        public static List<AssetFieldOutcome> WriteThermal(Document doc, Material material,
                                                           JObject fields, bool duplicateWhenShared,
                                                           out string refusal)
        {
            refusal = null;
            var outcomes = new List<AssetFieldOutcome>();
            if (doc == null || material == null || fields == null || !fields.Properties().Any()) return outcomes;

            PropertySetElement set = SetOf(doc, material.ThermalAssetId);
            ThermalAsset asset = null;
            if (set != null) { try { asset = set.GetThermalAsset(); } catch { } }

            if (asset == null)
            {
                string wanted = fields.Value<string>("material_type");
                if (string.IsNullOrWhiteSpace(wanted))
                {
                    refusal = "this material has no thermal asset, and creating one needs a MATERIAL TYPE: it " +
                              "decides which fields exist. Send it as thermal.material_type, one of: " +
                              string.Join(", ", ThermalTypes) + ". Nothing was written.";
                    return outcomes;
                }

                ThermalMaterialType type;
                if (!Enum.TryParse(wanted, true, out type) ||
                    !ThermalTypes.Contains(type.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    refusal = "'" + wanted + "' is not a thermal material type this build accepts. The " +
                              "supported ones are: " + string.Join(", ", ThermalTypes) + ".";
                    return outcomes;
                }

                try
                {
                    string name = fields.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name)) name = material.Name + " thermal";
                    var created = new ThermalAsset(name, type);
                    PropertySetElement element = PropertySetElement.Create(doc, created);
                    material.ThermalAssetId = element.Id;
                    set = element;
                    asset = element.GetThermalAsset();
                    outcomes.Add(new AssetFieldOutcome
                    {
                        Field = "(asset)",
                        State = Updated,
                        Now = "property set " + Rid.Value(element.Id) + ", type " + type,
                        Reason = "created because this material had none, with the type this call named."
                    });
                }
                catch (Exception ex)
                {
                    refusal = "the thermal asset could not be created (" + ex.Message + ").";
                    return outcomes;
                }
            }

            List<long> others = UsersOfThermal(doc, set.Id)
                .Where(id => id != Rid.Value(material.Id)).ToList();
            if (others.Count > 0 && !duplicateWhenShared)
            {
                refusal = "this thermal asset is shared with " + others.Count + " other material(s) (" +
                          string.Join(", ", others.Take(20)) + (others.Count > 20 ? ", …" : "") +
                          "). Editing it changes every one of them. Nothing was written. Send " +
                          "duplicate_shared_assets=true to give THIS material its own copy first.";
                return outcomes;
            }

            if (others.Count > 0)
            {
                try
                {
                    PropertySetElement copy = PropertySetElement.Create(doc, asset);
                    material.ThermalAssetId = copy.Id;
                    set = copy;
                    asset = copy.GetThermalAsset();
                    outcomes.Add(new AssetFieldOutcome
                    {
                        Field = "(asset)",
                        State = Updated,
                        Now = "property set " + Rid.Value(copy.Id),
                        Reason = "this material now has its OWN thermal asset, copied from the shared one."
                    });
                }
                catch (Exception ex)
                {
                    refusal = "the shared thermal asset could not be duplicated (" + ex.Message +
                              "), so nothing was written.";
                    return outcomes;
                }
            }

            foreach (JProperty field in fields.Properties())
                outcomes.Add(SetThermalField(asset, field));

            try { set.SetThermalAsset(asset); }
            catch (Exception ex)
            {
                refusal = "the edited thermal asset was refused by Revit (" + ex.Message + ").";
                return outcomes;
            }

            ThermalAsset afterAsset = null;
            try { afterAsset = set.GetThermalAsset(); } catch { }
            foreach (AssetFieldOutcome outcome in outcomes)
                if (outcome.State == Updated && outcome.Field != "(asset)")
                    outcome.Now = ReadThermalField(afterAsset, outcome.Field);

            return outcomes;
        }

        // =====================================================================
        // The field tables - CLOSED, and a name outside them is reported
        // =====================================================================

        private static AssetFieldOutcome SetStructuralField(StructuralAsset asset, JProperty field)
        {
            var outcome = new AssetFieldOutcome { Field = field.Name };
            double value;
            if (!TryNumber(field.Value, out value))
            {
                outcome.State = Refused;
                outcome.Reason = "this field takes a number and received " + field.Value.Type + ".";
                return outcome;
            }

            outcome.Was = ReadStructuralField(asset, field.Name);
            try
            {
                switch (field.Name)
                {
                    case "density": asset.Density = value; break;
                    case "minimum_yield_stress": asset.MinimumYieldStress = value; break;
                    case "minimum_tensile_strength": asset.MinimumTensileStrength = value; break;
                    case "concrete_compression": asset.ConcreteCompression = value; break;
                    case "young_modulus": asset.YoungModulus = Uniform(value); break;
                    case "poisson_ratio": asset.PoissonRatio = Uniform(value); break;
                    case "shear_modulus": asset.ShearModulus = Uniform(value); break;
                    case "thermal_expansion_coefficient":
                        asset.ThermalExpansionCoefficient = Uniform(value); break;
                    default:
                        // NOT SILENTLY DROPPED. A field this build does not know is named, so
                        // a caller who misspelled one finds out instead of reading a success.
                        outcome.State = UnknownField;
                        outcome.Reason = "this build writes density, minimum_yield_stress, " +
                                         "minimum_tensile_strength, concrete_compression, young_modulus, " +
                                         "poisson_ratio, shear_modulus and thermal_expansion_coefficient on a " +
                                         "structural asset. '" + field.Name + "' is not one of them and was " +
                                         "NOT written.";
                        return outcome;
                }
                outcome.State = Updated;
            }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the value: " + ex.Message + " Several of these fields are " +
                                 "constrained by the asset's class - a value valid for steel is not valid " +
                                 "for concrete.";
            }
            return outcome;
        }

        private static AssetFieldOutcome SetThermalField(ThermalAsset asset, JProperty field)
        {
            var outcome = new AssetFieldOutcome { Field = field.Name };
            double value;
            if (!TryNumber(field.Value, out value))
            {
                outcome.State = Refused;
                outcome.Reason = "this field takes a number and received " + field.Value.Type + ".";
                return outcome;
            }

            outcome.Was = ReadThermalField(asset, field.Name);
            try
            {
                switch (field.Name)
                {
                    case "thermal_conductivity": asset.ThermalConductivity = value; break;
                    case "specific_heat": asset.SpecificHeat = value; break;
                    case "density": asset.Density = value; break;
                    case "emissivity": asset.Emissivity = value; break;
                    case "permeability": asset.Permeability = value; break;
                    case "porosity": asset.Porosity = value; break;
                    case "reflectivity": asset.Reflectivity = value; break;
                    default:
                        outcome.State = UnknownField;
                        outcome.Reason = "this build writes thermal_conductivity, specific_heat, density, " +
                                         "emissivity, permeability, porosity and reflectivity on a thermal " +
                                         "asset. '" + field.Name + "' is not one of them and was NOT written.";
                        return outcome;
                }
                outcome.State = Updated;
            }
            catch (Exception ex)
            {
                outcome.State = Refused;
                outcome.Reason = "Revit refused the value: " + ex.Message;
            }
            return outcome;
        }

        private static string ReadStructuralField(StructuralAsset asset, string name)
        {
            if (asset == null) return null;
            switch (name)
            {
                case "density": return Text(() => asset.Density);
                case "minimum_yield_stress": return Text(() => asset.MinimumYieldStress);
                case "minimum_tensile_strength": return Text(() => asset.MinimumTensileStrength);
                case "concrete_compression": return Text(() => asset.ConcreteCompression);
                case "young_modulus": return Text(() => asset.YoungModulus.X);
                case "poisson_ratio": return Text(() => asset.PoissonRatio.X);
                case "shear_modulus": return Text(() => asset.ShearModulus.X);
                case "thermal_expansion_coefficient": return Text(() => asset.ThermalExpansionCoefficient.X);
                default: return null;
            }
        }

        private static string ReadThermalField(ThermalAsset asset, string name)
        {
            if (asset == null) return null;
            switch (name)
            {
                case "thermal_conductivity": return Text(() => asset.ThermalConductivity);
                case "specific_heat": return Text(() => asset.SpecificHeat);
                case "density": return Text(() => asset.Density);
                case "emissivity": return Text(() => asset.Emissivity);
                case "permeability": return Text(() => asset.Permeability);
                case "porosity": return Text(() => asset.Porosity);
                case "reflectivity": return Text(() => asset.Reflectivity);
                default: return null;
            }
        }

        // =====================================================================

        private static PropertySetElement SetOf(Document doc, ElementId id)
        {
            if (doc == null || id == null || Rid.Value(id) < 0) return null;
            return doc.GetElement(id) as PropertySetElement;
        }

        /// <summary>
        /// An isotropic vector: the same value on all three axes.
        ///
        /// These properties are XYZ because Revit supports orthotropic materials. A single
        /// number means isotropic, and it is written to all three rather than to X alone -
        /// writing one axis leaves a material that is isotropic in name and orthotropic in
        /// its numbers.
        /// </summary>
        private static XYZ Uniform(double value) => new XYZ(value, value, value);

        private static bool TryNumber(JToken token, out double value)
        {
            value = 0;
            if (token == null) return false;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                value = token.Value<double>();
                return true;
            }
            return false;
        }

        private static JToken Safe(Func<string> read)
        {
            try { string v = read(); return v == null ? (JToken)JValue.CreateNull() : v; }
            catch { return JValue.CreateNull(); }
        }

        private static JToken Number(Func<double> read)
        {
            try { return read(); } catch { return JValue.CreateNull(); }
        }

        /// <summary>An XYZ property, reported as its three components or as null.</summary>
        private static JToken Vector(Func<XYZ> read)
        {
            try
            {
                XYZ v = read();
                return v == null ? (JToken)JValue.CreateNull()
                    : new JObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
            }
            catch { return JValue.CreateNull(); }
        }

        private static string Text(Func<double> read)
        {
            try { return read().ToString("0.######", CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
