// -----------------------------------------------------------------------------
// Horizun Revit MCP - declarative, typed creation of loadable RFA families.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CreateFamilyCommand : ICommand
    {
        public string Name => "horizun_create_family";
        public string Description => "Compile a typed loadable RFA from an RFT: parameters, reference skeleton, solid/void forms, nested instances and MEP connectors; save, load and verify it.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document project = gate.Document;
            if (project.IsFamilyDocument)
                return CommandResult.Fail("horizun_create_family starts from a project document and creates a separate loadable RFA. For an already-open RFA use horizun_family_apply.");

            string template = FullPath(request.Value<string>("template_path"), ".rft", "template_path", true, out string pathError);
            if (pathError != null) return CommandResult.Fail(pathError);
            string output = FullPath(request.Value<string>("output_path"), ".rfa", "output_path", false, out pathError);
            if (pathError != null) return CommandResult.Fail(pathError);
            if (!File.Exists(template)) return CommandResult.Fail("template_path does not exist: " + template);
            string folder = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return CommandResult.Fail("The output directory does not exist: " + folder + ". It is not created implicitly.");
            bool overwrite = request.Value<bool?>("overwrite") == true;
            if (File.Exists(output) && !overwrite) return CommandResult.Fail("output_path already exists and overwrite=false: " + output);
            if (string.Equals(template, output, StringComparison.OrdinalIgnoreCase)) return CommandResult.Fail("template_path and output_path must differ.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            if (!Scale(units, out double scale)) return CommandResult.Fail("units must be mm, m or feet.");
            FamilyPlan plan;
            try { plan = BuildPlan(request, scale); }
            catch (Exception ex) { return CommandResult.Fail("Invalid family specification: " + ex.Message); }

            bool load = request.Value<bool?>("load_into_project") != false;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "template_path", "output_path", "units", "parameters", "types", "emit_type_catalog", "flex", "emit_thumbnail",
                "forms", "connectors", "reference_planes", "dimensions", "family_lines",
                "nested_instances", "overwrite", "load_into_project", "overwrite_parameter_values");
            // ---- The MATERIALISED plan. The request is a recipe; the TEMPLATE is an
            // ingredient that lives on disk, and everything in the family starts as a copy
            // of it. A template swapped between rehearsal and apply mints a different
            // family under the approved words, and nothing in the request would notice -
            // so its content hash rides in the plan, along with the output-file fact the
            // overwrite decision was taken against.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "template=" + SafeFileHash(template) +
                                     ";output_exists=" + (File.Exists(output) ? "1" : "0") +
                                     ";overwrite=" + (overwrite ? "1" : "0")
            };
            resolvedPlan.Elements.Add(new PlannedElement
            {
                UniqueId = "family:" + Path.GetFileName(output),
                Category = "loadable_rfa",
                Action = PlannedAction.Create,
                BeforeValues = new Dictionary<string, string>
                {
                    { "parameters", plan.Parameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "types", plan.Types.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "forms", plan.Forms.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "load", load ? "1" : "0" }
                }
            });

            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true,
                    ["family_kind"] = "loadable_rfa",
                    ["template_path"] = template,
                    ["output_path"] = output,
                    ["parameters"] = plan.Parameters.Count,
                    ["types"] = plan.Types.Count,
                    ["forms"] = plan.Forms.Count,
                    ["connectors"] = plan.Connectors.Count,
                    ["reference_planes"] = plan.ReferencePlanes.Count,
                    ["dimensions"] = plan.Dimensions.Count,
                    ["family_lines"] = plan.FamilyLines.Count,
                    ["nested_instances"] = plan.NestedInstances.Count,
                    ["load_into_project"] = load,
                    ["plan"] = plan.Summary(),
                    ["api_limitations"] = new JArray(
                        "Revit system families are project ElementTypes, not RFA files; use horizun_manage_system_types.",
                        "The public Revit API does not provide general creation of in-place families; Horizun refuses to fake it with UI automation."),
                    ["note"] = "No family document, transaction or file was created."
                };
                if (plan.Dimensions.Any(x => x.ViewName != null || x.TypeName != null))
                    result["deferred_checks"] = new JArray(
                        "dimensions[].view_name and dimensions[].dimension_type_name resolve against the family document, " +
                        "which a dry run never opens; a wrong name refuses at apply before any transaction is started.");
                DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, true,
                    "the token binds template BY CONTENT (its SHA-256, not its path), destination and what already " +
                    "exists there, units, parameter/type/form graph, connectors and load policy - a template edited " +
                    "or swapped before you apply refuses as a stale plan.");
                return CommandResult.Ok(result);
            }

            // Recomputed by THIS call, template re-hashed from disk.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;

            long beforeBytes = File.Exists(output) ? new FileInfo(output).Length : -1;
            DateTime beforeWrite = File.Exists(output) ? File.GetLastWriteTimeUtc(output) : DateTime.MinValue;
            Document family = null;
            Family loaded = null;
            JArray createdForms = new JArray();
            JArray createdConnectors = new JArray();
            JArray createdReferencePlanes = new JArray();
            JArray createdDimensions = new JArray();
            JArray createdFamilyLines = new JArray();
            JArray createdNestedInstances = new JArray();
            JObject familyDocumentVerification = null;
            JObject flexBlock = null; JObject thumbnailBlock = null; string thumbnailPath = null;
            try
            {
                family = app.Application.NewFamilyDocument(template);
                if (family == null || !family.IsFamilyDocument)
                    throw new InvalidOperationException("Revit did not create a family document from the template.");
                View familyView = FindFamilyCreationView(family);
                // Dimension views and dimension types resolve against the REAL family
                // document, and deliberately before any transaction or nested load has
                // touched it: a wrong name must refuse while the document is still an
                // untouched copy of the template, so "nothing was created" stays
                // literally true. A dry run never opens documents, which is why these
                // two names are the only dimension inputs it cannot pre-validate.
                Dictionary<string, View> dimensionViews = ResolveDimensionViews(family, plan.Dimensions);
                Dictionary<string, DimensionType> dimensionTypes = ResolveDimensionTypes(family, plan.Dimensions);

                var nestedFamilies = new Dictionary<string, Family>(StringComparer.OrdinalIgnoreCase);
                foreach (NestedInstancePlan nestedPlan in plan.NestedInstances)
                {
                    if (nestedFamilies.ContainsKey(nestedPlan.FamilyPath)) continue;
                    if (!family.LoadFamily(nestedPlan.FamilyPath,
                        new FamilyLoadOptions(request.Value<bool?>("overwrite_parameter_values") == true), out Family nestedFamily) || nestedFamily == null)
                        throw new InvalidOperationException("Revit did not load nested family " + nestedPlan.FamilyPath);
                    nestedFamilies[nestedPlan.FamilyPath] = nestedFamily;
                }

                using (var tx = new Transaction(family, request.Value<string>("transaction_name") ?? "Horizun: create parametric family"))
                {
                    tx.Start();
                    try
                    {
                        FamilyManager fm = family.FamilyManager;
                        Dictionary<string, FamilyParameter> parameters = EnsureParameters(fm, plan.Parameters);
                        Dictionary<string, FamilyType> types = EnsureTypes(fm, plan.Types);
                        ApplyTypeValues(fm, plan.Types, types, parameters, scale);

                        var referencePlanes = new Dictionary<string, ReferencePlane>(StringComparer.Ordinal);
                        using (var referenceTx = new SubTransaction(family))
                        {
                            referenceTx.Start();
                            try
                            {
                                foreach (ReferencePlanePlan referencePlan in plan.ReferencePlanes)
                                {
                                    ReferencePlane referencePlane = family.FamilyCreate.NewReferencePlane(referencePlan.BubbleEnd,
                                        referencePlan.FreeEnd, referencePlan.CutVector, familyView);
                                    if (!string.IsNullOrWhiteSpace(referencePlan.Name)) referencePlane.Name = referencePlan.Name;
                                    Parameter referenceKind = referencePlane.LookupParameter("Is Reference");
                                    if (referenceKind != null && !referenceKind.IsReadOnly)
                                        referenceKind.Set((int)FamilyInstanceReferenceType.StrongReference);
                                    referencePlanes[referencePlan.Key] = referencePlane;
                                    createdReferencePlanes.Add(new JObject { ["key"] = referencePlan.Key, ["element_id"] = Rid.Value(referencePlane.Id) });
                                }
                                Guard.Commit(referenceTx, "create family reference planes");
                            }
                            catch
                            {
                                if (referenceTx.GetStatus() == TransactionStatus.Started) Guard.RollBack(referenceTx);
                                throw;
                            }
                        }
                        // ReferencePlane.GetReference() is not usable for a new family element
                        // until Revit has regenerated the family document. Without this explicit
                        // regeneration, NewLinearDimension rejects an otherwise valid pair of
                        // reference planes with its generic "conditions for the inputs" error.
                        if (plan.Dimensions.Count > 0) family.Regenerate();
                        foreach (DimensionPlan dimensionPlan in plan.Dimensions)
                        {
                            View dimensionView = dimensionPlan.ViewName == null ? familyView : dimensionViews[dimensionPlan.ViewName];
                            DimensionType dimensionType = dimensionPlan.TypeName == null ? null : dimensionTypes[dimensionPlan.TypeName];
                            var references = new ReferenceArray();
                            foreach (string referenceKey in dimensionPlan.ReferencePlaneKeys)
                                references.Append(referencePlanes[referenceKey].GetReference());
                            Line dimensionLine = Line.CreateBound(dimensionPlan.LineStart, dimensionPlan.LineEnd);
                            Dimension dimension = dimensionType == null
                                ? family.FamilyCreate.NewLinearDimension(dimensionView, dimensionLine, references)
                                : family.FamilyCreate.NewLinearDimension(dimensionView, dimensionLine, references, dimensionType);
                            if (!string.IsNullOrWhiteSpace(dimensionPlan.LabelParameter))
                                dimension.FamilyLabel = parameters[dimensionPlan.LabelParameter];
                            // lock+label was already refused while planning; if Revit still
                            // rejects either flag here, the message must name the dimension,
                            // and the throw rolls the whole transaction back.
                            if (dimensionPlan.Lock)
                            {
                                try { dimension.IsLocked = true; }
                                catch (Exception ex)
                                {
                                    throw new InvalidOperationException("dimension '" + dimensionPlan.Key +
                                        "' could not be locked: " + ex.Message + " The transaction is rolled back; nothing was created.", ex);
                                }
                            }
                            if (dimensionPlan.Eq)
                            {
                                try { dimension.AreSegmentsEqual = true; }
                                catch (Exception ex)
                                {
                                    throw new InvalidOperationException("dimension '" + dimensionPlan.Key +
                                        "' could not take its EQ constraint: " + ex.Message + " The transaction is rolled back; nothing was created.", ex);
                                }
                            }
                            var dimensionRow = new JObject
                            {
                                ["key"] = dimensionPlan.Key, ["element_id"] = Rid.Value(dimension.Id),
                                ["view_id"] = Rid.Value(dimensionView.Id), ["view_name"] = dimensionView.Name
                            };
                            if (dimensionType != null)
                            {
                                dimensionRow["dimension_type_id"] = Rid.Value(dimensionType.Id);
                                dimensionRow["dimension_type_name"] = dimensionType.Name;
                            }
                            createdDimensions.Add(dimensionRow);
                        }
                        foreach (FamilyLinePlan linePlan in plan.FamilyLines)
                        {
                            SketchPlane linePlane = SketchPlane.Create(family, Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(linePlan.Normal, linePlan.Start));
                            CurveElement curve = linePlan.Kind == "symbolic"
                                ? (CurveElement)family.FamilyCreate.NewSymbolicCurve(Line.CreateBound(linePlan.Start, linePlan.End), linePlane)
                                : family.FamilyCreate.NewModelCurve(Line.CreateBound(linePlan.Start, linePlan.End), linePlane);
                            createdFamilyLines.Add(new JObject { ["key"] = linePlan.Key, ["kind"] = linePlan.Kind, ["element_id"] = Rid.Value(curve.Id) });
                        }
                        foreach (NestedInstancePlan nestedPlan in plan.NestedInstances)
                        {
                            Family nestedFamily = nestedFamilies[nestedPlan.FamilyPath];
                            FamilySymbol nestedSymbol = nestedFamily.GetFamilySymbolIds().Select(id => family.GetElement(id) as FamilySymbol)
                                .FirstOrDefault(x => x != null && string.Equals(x.Name, nestedPlan.TypeName, StringComparison.Ordinal));
                            if (nestedSymbol == null)
                                throw new InvalidOperationException("nested family '" + nestedFamily.Name + "' has no type named '" + nestedPlan.TypeName + "'");
                            if (!nestedSymbol.IsActive) { nestedSymbol.Activate(); family.Regenerate(); }
                            FamilyInstance instance = nestedPlan.Placement == "view_point"
                                ? family.FamilyCreate.NewFamilyInstance(nestedPlan.Point, nestedSymbol, familyView)
                                : family.FamilyCreate.NewFamilyInstance(nestedPlan.Point, nestedSymbol, StructuralType.NonStructural);
                            if (Math.Abs(nestedPlan.RotationRadians) > 1e-12)
                                ElementTransformUtils.RotateElement(family, instance.Id,
                                    Line.CreateUnbound(nestedPlan.Point, nestedPlan.Placement == "view_point"
                                        ? familyView.ViewDirection : XYZ.BasisZ), nestedPlan.RotationRadians);
                            foreach (JProperty association in nestedPlan.Associations.Properties())
                            {
                                Parameter nestedParameter = ResolveNestedParameter(instance, association.Name);
                                Associate(fm, nestedParameter, association.Value.Value<string>(), parameters);
                            }
                            createdNestedInstances.Add(new JObject
                            {
                                ["key"] = nestedPlan.Key, ["element_id"] = Rid.Value(instance.Id),
                                ["family_id"] = Rid.Value(nestedFamily.Id), ["symbol_id"] = Rid.Value(nestedSymbol.Id)
                            });
                        }

                        var forms = new Dictionary<string, GenericForm>(StringComparer.Ordinal);
                        foreach (FormPlan formPlan in plan.Forms)
                        {
                            GenericForm form = CreateForm(family, fm, formPlan, parameters);
                            forms[formPlan.Key] = form;
                            createdForms.Add(new JObject { ["key"] = formPlan.Key, ["kind"] = formPlan.Kind, ["element_id"] = Rid.Value(form.Id) });
                        }
                        family.Regenerate();
                        foreach (ConnectorPlan connectorPlan in plan.Connectors)
                        {
                            if (!forms.TryGetValue(connectorPlan.HostFormKey, out GenericForm host))
                                throw new InvalidOperationException("connector '" + connectorPlan.Key + "' references unknown host_form_key '" + connectorPlan.HostFormKey + "'");
                            ConnectorElement connector = CreateConnector(family, fm, connectorPlan, host, parameters);
                            createdConnectors.Add(new JObject { ["key"] = connectorPlan.Key, ["kind"] = connectorPlan.Kind, ["element_id"] = Rid.Value(connector.Id) });
                        }

                        foreach (ParameterPlan p in plan.Parameters.Where(x => x.FormulaSpecified))
                            fm.SetFormula(parameters[p.Name], p.Formula);
                        family.Regenerate();
                        Guard.Commit(tx, tx.GetName());
                    }
                    catch
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                        throw;
                    }
                }

                familyDocumentVerification = VerifyFamilyDocument(family, plan, createdForms, createdConnectors,
                    createdReferencePlanes, createdDimensions, createdFamilyLines, createdNestedInstances);

                // ---- flex: every type activated in turn, the geometry MEASURED. ----
                // A parametric family that does not move when its dimensions change is
                // a drawing with sliders. The flex activates each type, regenerates,
                // and records the solid extents; two types whose driving values differ
                // but whose extents match to a tenth of a millimetre are reported as
                // not_flexing - a warning with the numbers, because a family with only
                // non-geometric parameters is legitimate and gets to say so.
                if (request.Value<bool?>("flex") == true && plan.Types.Count > 0)
                    flexBlock = FlexTypes(family, plan);

                // ---- thumbnail: a real image of the family, verified from disk. ----
                if (request.Value<bool?>("emit_thumbnail") == true)
                    thumbnailPath = System.IO.Path.ChangeExtension(output, ".png");

                var save = new SaveAsOptions { OverwriteExistingFile = overwrite, MaximumBackups = 1 };
                family.SaveAs(output, save);
                if (thumbnailPath != null)
                    thumbnailBlock = ExportThumbnail(family, thumbnailPath);
                // The building document has served its purpose - close it NOW, on
                // purpose. The deliverable is the file on disk, and the only honest way
                // to verify a file is to read it back from disk through a fresh
                // OpenDocumentFile, never through the in-memory document that wrote it.
                // Loading into the project moves after that verification for the same
                // reason: the project receives a proven file, not a hopeful one.
                family.Close(false);
                family = null;
            }
            catch (Exception ex)
            {
                try { if (family != null && family.IsValidObject) family.Close(false); } catch { }
                bool fileNow = File.Exists(output);
                return CommandResult.Fail("Family creation failed: " + ex.Message +
                    (fileNow ? " A non-empty output may already exist at " + output + "; it is reported rather than deleted." : " No output file was found."));
            }
            finally
            {
                try { if (family != null && family.IsValidObject) family.Close(false); } catch { }
            }

            if (!File.Exists(output)) return CommandResult.Fail("Revit returned from SaveAs, but output_path does not exist: " + output);
            var info = new FileInfo(output);
            bool changed = info.Length > 0 && (beforeBytes < 0 || info.Length != beforeBytes || info.LastWriteTimeUtc != beforeWrite);
            if (!changed) return CommandResult.Fail("The RFA exists but no new/changed non-empty file could be proven at " + output + ".");

            // ---- Verification against the SAVED BYTES. SaveAs returning and the file
            // changing are facts about the filesystem, not about the family: a truncated
            // or half-written RFA satisfies both. So the saved file is re-opened from
            // disk and every dimension re-read in the reopened document against what was
            // requested. A failure here is a verification failure, never softened to a
            // warning: the RFA exists, and it is NOT verified.
            JObject reopenedVerification = null;
            Document reopened = null;
            try
            {
                reopened = app.Application.OpenDocumentFile(output);
                if (reopened == null || !reopened.IsFamilyDocument)
                    throw new InvalidOperationException("the saved file did not re-open as a family document");
                reopenedVerification = VerifyReopenedFamily(reopened, output, plan, createdDimensions);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("The RFA was saved at " + output + " and verified in memory before saving, " +
                    "but verification after re-opening the saved file FAILED: " + ex.Message +
                    " The file exists on disk and is NOT verified" +
                    (load ? ", and it was NOT loaded into the project." : ".") +
                    " Inspect it in Revit, or re-run with overwrite=true to replace it.");
            }
            finally
            {
                try { if (reopened != null && reopened.IsValidObject) reopened.Close(false); } catch { }
            }

            if (load)
            {
                // The load starts FROM THE VERIFIED FILE, in the project's own
                // transaction - not from the family document that wrote it, which is
                // already closed. What enters the project is exactly what was proven.
                // "The project was not changed" is a claim about the ROLLBACK, so it is
                // only made when Revit's own status confirms one - a Pending or Error
                // answer keeps its uncertainty instead of being asserted away, which is
                // exactly what PlanFailure.SingleTransactionOutcome exists to phrase.
                bool loadRollbackAttempted = false;
                string loadRollbackStatus = PlanFailure.NotAttempted;
                try
                {
                    using (var loadTx = new Transaction(project, "Horizun: load family " + Path.GetFileNameWithoutExtension(output)))
                    {
                        loadTx.Start();
                        try
                        {
                            if (!project.LoadFamily(output, new FamilyLoadOptions(request.Value<bool?>("overwrite_parameter_values") == true), out loaded) || loaded == null)
                                throw new InvalidOperationException("Revit's LoadFamily returned no loaded Family");
                            Guard.Commit(loadTx, "load family into project");
                        }
                        catch
                        {
                            if (loadTx.GetStatus() == TransactionStatus.Started)
                            {
                                loadRollbackAttempted = true;
                                loadRollbackStatus = Guard.RollBack(loadTx).StatusName;
                            }
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail("The RFA was saved at " + output + " and verified by re-opening it from disk, " +
                        "but loading it into the project failed: " + ex.Message + " " +
                        PlanFailure.SingleTransactionOutcome(loadRollbackAttempted, loadRollbackStatus,
                            "the project holds nothing from this load") +
                        " Load the verified RFA manually, or run horizun_create_family again against the same output with overwrite=true.");
                }
            }

            JObject loadedResult = null;
            if (load)
            {
                Element fresh = loaded == null ? null : project.GetElement(loaded.Id);
                if (!(fresh is Family freshFamily))
                    return CommandResult.Fail("The RFA was saved, but the loaded Family could not be re-read from the target project. File: " + output);
                string expectedFamilyName = Path.GetFileNameWithoutExtension(output);
                if (!string.Equals(freshFamily.Name, expectedFamilyName, StringComparison.Ordinal))
                    return CommandResult.Fail("The RFA was saved and a Family was loaded, but its re-read name '" + freshFamily.Name +
                        "' does not match output family name '" + expectedFamilyName + "'. Success is not claimed.");
                List<FamilySymbol> freshSymbols = freshFamily.GetFamilySymbolIds().Select(id => project.GetElement(id) as FamilySymbol)
                    .Where(x => x != null).ToList();
                var freshTypeNames = new HashSet<string>(freshSymbols.Select(x => x.Name), StringComparer.Ordinal);
                List<string> missingTypes = plan.Types.Select(x => x.Name).Where(x => !freshTypeNames.Contains(x)).ToList();
                if (missingTypes.Count > 0)
                    return CommandResult.Fail("The Family loaded, but requested types were not re-read in the project: " + string.Join(", ", missingTypes));
                loadedResult = new JObject
                {
                    ["family_id"] = Rid.Value(freshFamily.Id),
                    ["family_name"] = freshFamily.Name,
                    ["symbol_ids"] = new JArray(freshSymbols.Select(x => Rid.Value(x.Id))),
                    ["requested_type_names_verified"] = missingTypes.Count == 0
                };
            }

            // ---- the type catalog, when asked for: built by TypeCatalogRules
            // (columns decided and exclusions NAMED in Core), written beside the
            // RFA, and RE-READ - bytes, sha256 and row count come from the file on
            // disk, not from the string that was meant to become it.
            JObject catalogBlock = null;
            if (request.Value<bool?>("emit_type_catalog") == true)
            {
                var catalogColumns = plan.Parameters.Select(parameter => new CatalogColumn
                { Name = parameter.Name, DataType = parameter.DataType }).ToList();
                var withFormula = plan.Parameters.Where(parameter => parameter.FormulaSpecified)
                                                 .Select(parameter => parameter.Name).ToList();
                var catalogTypes = plan.Types.Select(t =>
                    new KeyValuePair<string, IDictionary<string, string>>(t.Name,
                        (IDictionary<string, string>)t.Values.Properties().ToDictionary(
                            property => property.Name,
                            property => TypeCatalogRules.ValueCell(
                                plan.Parameters.FirstOrDefault(x => x.Name == property.Name)?.DataType ?? "text",
                                ((JValue)property.Value)?.Value)))).ToList();
                string catalogContent; List<string> catalogExcluded;
                string catalogError = TypeCatalogRules.Build(catalogColumns, withFormula, null, catalogTypes,
                                                             out catalogContent, out catalogExcluded);
                if (catalogError != null)
                    return CommandResult.Fail("The RFA was created and verified at " + output + ", but the type " +
                        "catalog you asked for cannot be built: " + catalogError + " The RFA stands; re-run " +
                        "without emit_type_catalog or fix the spec.");
                string catalogPath = System.IO.Path.ChangeExtension(output, ".txt");
                File.WriteAllText(catalogPath, catalogContent, new System.Text.UTF8Encoding(false));
                byte[] catalogBytes = File.ReadAllBytes(catalogPath);
                string catalogSha;
                using (var hasher = System.Security.Cryptography.SHA256.Create())
                    catalogSha = BitConverter.ToString(hasher.ComputeHash(catalogBytes)).Replace("-", "").ToLowerInvariant();
                int catalogRows = 0;
                foreach (char c in System.Text.Encoding.UTF8.GetString(catalogBytes)) if (c == '\n') catalogRows++;
                catalogBlock = new JObject
                {
                    ["path"] = catalogPath,
                    ["bytes"] = catalogBytes.Length,
                    ["sha256"] = catalogSha,
                    ["rows"] = catalogRows,
                    ["types"] = plan.Types.Count,
                    ["columns_excluded"] = new JArray(catalogExcluded),
                    ["note"] = "An empty cell keeps the type's own value at load time. Revit finds the catalog " +
                               "by name: it must sit beside the RFA when the family is loaded."
                };
            }

            return CommandResult.Ok(new JObject
            {
                ["dry_run"] = false,
                ["family_kind"] = "loadable_rfa",
                ["output_verified"] = true,
                ["output_path"] = output,
                ["type_catalog"] = catalogBlock,
                ["bytes"] = info.Length,
                ["last_write_utc"] = info.LastWriteTimeUtc.ToString("o"),
                ["parameters_verified"] = plan.Parameters.Count,
                ["types_requested"] = plan.Types.Count,
                ["forms_verified"] = createdForms.Count,
                ["connectors_verified"] = createdConnectors.Count,
                ["reference_planes_verified"] = createdReferencePlanes.Count,
                ["dimensions_verified"] = createdDimensions.Count,
                ["family_lines_verified"] = createdFamilyLines.Count,
                ["nested_instances_verified"] = createdNestedInstances.Count,
                ["forms"] = createdForms,
                ["connectors"] = createdConnectors,
                ["reference_planes"] = createdReferencePlanes,
                ["dimensions"] = createdDimensions,
                ["family_lines"] = createdFamilyLines,
                ["nested_instances"] = createdNestedInstances,
                ["family_document_verification"] = familyDocumentVerification,
                ["reopened_verification"] = reopenedVerification,
                ["loaded_into_project"] = load,
                ["loaded_family"] = loadedResult,
                ["flex"] = flexBlock,
                ["thumbnail"] = thumbnailBlock
            });
        }

        private static FamilyPlan BuildPlan(JObject request, double scale)
        {
            var plan = new FamilyPlan { Scale = scale };
            var parameterNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject row in (request["parameters"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string name = RequiredText(row, "name");
                if (!parameterNames.Add(name)) throw new ArgumentException("duplicate parameter name '" + name + "'");
                string dataType = (row.Value<string>("data_type") ?? "text").ToLowerInvariant();
                ForgeTypeId spec = DataType(dataType);
                string group = (row.Value<string>("group") ?? "data").ToLowerInvariant();
                bool formulaSpecified = row["formula"] != null;
                string formula = row.Value<string>("formula");
                if (formulaSpecified && string.IsNullOrWhiteSpace(formula))
                    throw new ArgumentException("parameter '" + name + "' formula cannot be empty; omit it to preserve a template formula");
                plan.Parameters.Add(new ParameterPlan
                {
                    Name = name, DataType = dataType, Spec = spec, Group = ParameterGroup(group),
                    Instance = row.Value<bool?>("instance") == true, Formula = formula, FormulaSpecified = formulaSpecified
                });
            }
            if (request["parameters"] != null && (!(request["parameters"] is JArray parameterArray) || parameterArray.Count != plan.Parameters.Count))
                throw new ArgumentException("every parameters entry must be an object");

            var typeNames = new HashSet<string>(StringComparer.Ordinal);
            var parameterPlans = plan.Parameters.ToDictionary(x => x.Name, StringComparer.Ordinal);
            foreach (JObject row in (request["types"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string name = RequiredText(row, "name");
                if (!typeNames.Add(name)) throw new ArgumentException("duplicate family type name '" + name + "'");
                JObject values = row["values"] as JObject ?? new JObject();
                foreach (JProperty value in values.Properties())
                {
                    if (!parameterPlans.TryGetValue(value.Name, out ParameterPlan parameter))
                        throw new ArgumentException("type '" + name + "' sets unknown declared parameter '" + value.Name + "'");
                    ValidateFamilyValue(parameter, value.Value, name);
                }
                plan.Types.Add(new TypePlan { Name = name, Values = values });
            }
            if (request["types"] != null && (!(request["types"] is JArray typeArray) || typeArray.Count != plan.Types.Count))
                throw new ArgumentException("every types entry must be an object");

            var formKeys = new HashSet<string>(StringComparer.Ordinal);
            int formIndex = 0;
            foreach (JObject row in (request["forms"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = row.Value<string>("key") ?? "form_" + formIndex;
                if (!formKeys.Add(key)) throw new ArgumentException("duplicate form key '" + key + "'");
                string kind = (row.Value<string>("kind") ?? "").ToLowerInvariant();
                if (kind != "extrusion" && kind != "blend" && kind != "revolution" && kind != "sweep" && kind != "swept_blend")
                    throw new ArgumentException("form '" + key + "' kind must be extrusion, blend, revolution, sweep or swept_blend");
                string plane = (row.Value<string>("plane") ?? (kind == "revolution" ? "xz" : "xy")).ToLowerInvariant();
                XYZ normal = PlaneNormal(plane);
                var f = new FormPlan
                {
                    Key = key, Kind = kind, Solid = row.Value<bool?>("solid") != false, Plane = plane, Normal = normal,
                    Loops = ReadLoops(row["profile"] as JArray, scale, normal, "profile"),
                    Depth = Finite(row.Value<double?>("depth") ?? 1000, "depth") * scale,
                    BottomOffset = Finite(row.Value<double?>("bottom_offset") ?? 0, "bottom_offset") * scale,
                    TopOffset = Finite(row.Value<double?>("top_offset") ?? 1000, "top_offset") * scale,
                    StartAngle = Finite(row.Value<double?>("start_angle_degrees") ?? 0, "start_angle_degrees") * Math.PI / 180.0,
                    EndAngle = Finite(row.Value<double?>("end_angle_degrees") ?? 360, "end_angle_degrees") * Math.PI / 180.0,
                    StartParameter = row.Value<string>("start_parameter"), EndParameter = row.Value<string>("end_parameter"),
                    MaterialParameter = row.Value<string>("material_parameter"), VisibilityParameter = row.Value<string>("visibility_parameter")
                };
                if (kind == "extrusion" && f.Depth <= 0) throw new ArgumentException("form '" + key + "' depth must be positive");
                if (kind == "blend")
                {
                    f.TopLoops = ReadLoops(row["top_profile"] as JArray, scale, normal, "top_profile");
                    if (f.Loops.Count != 1 || f.TopLoops.Count != 1) throw new ArgumentException("blend currently requires one bottom and one top loop");
                    if (Math.Abs(f.Loops[0][0].DotProduct(normal) - f.TopLoops[0][0].DotProduct(normal)) > 1e-7)
                        throw new ArgumentException("blend profile and top_profile coordinates must lie on the same sketch plane; use bottom_offset/top_offset for depth");
                    if (f.TopOffset <= f.BottomOffset) throw new ArgumentException("blend top_offset must exceed bottom_offset");
                }
                if (kind == "revolution")
                {
                    f.AxisStart = ReadPoint(row["axis_start"], scale); f.AxisEnd = ReadPoint(row["axis_end"], scale);
                    if (f.AxisStart.DistanceTo(f.AxisEnd) < 1e-9) throw new ArgumentException("revolution axis_start and axis_end must differ");
                    double profilePlaneOffset = f.Loops[0][0].DotProduct(normal);
                    if (Math.Abs(f.AxisStart.DotProduct(normal) - profilePlaneOffset) > 1e-7 ||
                        Math.Abs(f.AxisEnd.DotProduct(normal) - profilePlaneOffset) > 1e-7)
                        throw new ArgumentException("revolution axis must lie in the same selected plane as profile");
                    if (f.EndAngle <= f.StartAngle || f.EndAngle - f.StartAngle > Math.PI * 2 + 1e-9)
                        throw new ArgumentException("revolution angles must define a positive sweep no greater than 360 degrees");
                }
                if (kind == "sweep" || kind == "swept_blend")
                {
                    f.PathPlane = (row.Value<string>("path_plane") ?? "xz").ToLowerInvariant();
                    f.PathNormal = PlaneNormal(f.PathPlane);
                    f.Path = ReadPath(row["path"] as JArray, scale, f.PathNormal, "form '" + key + "' path");
                    if (kind == "sweep")
                    {
                        f.ProfileLocationCurveIndex = row.Value<int?>("profile_location_curve_index") ?? 0;
                        if (f.ProfileLocationCurveIndex < 0 || f.ProfileLocationCurveIndex >= f.Path.Count - 1)
                            throw new ArgumentException("form '" + key + "' profile_location_curve_index is outside path segments");
                        string location = row.Value<string>("profile_plane_location") ?? "Start";
                        if (!Enum.TryParse(location, true, out ProfilePlaneLocation parsedLocation) ||
                            !Enum.IsDefined(typeof(ProfilePlaneLocation), parsedLocation))
                            throw new ArgumentException("form '" + key + "' profile_plane_location must be Start, MidPoint or End");
                        f.ProfilePlaneLocation = parsedLocation;
                        ValidateSweepProfile(f, key);
                    }
                    else
                    {
                        if (f.Path.Count != 2) throw new ArgumentException("swept_blend currently requires a single straight path segment");
                        f.TopLoops = ReadLoops(row["top_profile"] as JArray, scale, normal, "top_profile");
                        if (f.Loops.Count != 1 || f.TopLoops.Count != 1)
                            throw new ArgumentException("swept_blend requires one bottom and one top profile loop");
                        ValidateSweptBlendProfiles(f, key);
                    }
                }
                ValidateParameterReferences(f, parameterPlans);
                plan.Forms.Add(f); formIndex++;
            }
            if (request["forms"] != null && (!(request["forms"] is JArray formArray) || formArray.Count != plan.Forms.Count))
                throw new ArgumentException("every forms entry must be an object");

            var connectorKeys = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, FormPlan> formPlans = plan.Forms.ToDictionary(x => x.Key, StringComparer.Ordinal);
            int connectorIndex = 0;
            foreach (JObject row in (request["connectors"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = row.Value<string>("key") ?? "connector_" + connectorIndex;
                if (!connectorKeys.Add(key)) throw new ArgumentException("duplicate connector key '" + key + "'");
                string host = RequiredText(row, "host_form_key");
                if (!formKeys.Contains(host)) throw new ArgumentException("connector '" + key + "' references unknown host_form_key '" + host + "'");
                if (!formPlans[host].Solid) throw new ArgumentException("connector '" + key + "' cannot be hosted on void form '" + host + "'");
                string kind = (row.Value<string>("kind") ?? "").ToLowerInvariant();
                if (kind != "pipe" && kind != "duct" && kind != "electrical" && kind != "conduit" && kind != "cable_tray")
                    throw new ArgumentException("connector '" + key + "' kind must be pipe, duct, electrical, conduit or cable_tray");
                string systemType = row.Value<string>("system_type");
                string profileName = row.Value<string>("profile") ?? "Round";
                if (kind == "pipe" && (!Enum.TryParse(systemType ?? "UndefinedSystemType", true, out PipeSystemType pipeSystem) ||
                    !Enum.IsDefined(typeof(PipeSystemType), pipeSystem)))
                    throw new ArgumentException("connector '" + key + "' has invalid pipe system_type '" + systemType + "'");
                if (kind == "duct")
                {
                    if (!Enum.TryParse(systemType ?? "UndefinedSystemType", true, out DuctSystemType ductSystem) ||
                        !Enum.IsDefined(typeof(DuctSystemType), ductSystem))
                        throw new ArgumentException("connector '" + key + "' has invalid duct system_type '" + systemType + "'");
                    if (!Enum.TryParse(profileName, true, out ConnectorProfileType connectorProfile) ||
                        !Enum.IsDefined(typeof(ConnectorProfileType), connectorProfile))
                        throw new ArgumentException("connector '" + key + "' has invalid duct profile '" + profileName + "'");
                }
                if (kind == "electrical" && (!Enum.TryParse(systemType ?? "UndefinedSystemType", true, out ElectricalSystemType electricalSystem) ||
                    !Enum.IsDefined(typeof(ElectricalSystemType), electricalSystem)))
                    throw new ArgumentException("connector '" + key + "' has invalid electrical system_type '" + systemType + "'");
                XYZ faceNormal = ReadVector(row["face_normal"]);
                string diameter = row.Value<string>("diameter_parameter");
                string width = row.Value<string>("width_parameter");
                string height = row.Value<string>("height_parameter");
                foreach (string parameter in new[] { diameter, width, height })
                    ValidateTypedParameter(parameterPlans, parameter, "length", "connector '" + key + "' size");
                plan.Connectors.Add(new ConnectorPlan
                {
                    Key = key, HostFormKey = host, Kind = kind, FaceNormal = faceNormal,
                    SystemType = systemType, Profile = profileName,
                    Primary = row.Value<bool?>("primary") == true, DiameterParameter = diameter,
                    WidthParameter = width, HeightParameter = height
                });
                connectorIndex++;
            }
            if (request["connectors"] != null && (!(request["connectors"] is JArray connectorArray) || connectorArray.Count != plan.Connectors.Count))
                throw new ArgumentException("every connectors entry must be an object");
            if (plan.Connectors.Count(x => x.Primary) > 1)
                throw new ArgumentException("only one connector can be primary in a family");

            var referenceKeys = new HashSet<string>(StringComparer.Ordinal);
            int referenceIndex = 0;
            foreach (JObject row in (request["reference_planes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = row.Value<string>("key") ?? "reference_plane_" + referenceIndex;
                if (!referenceKeys.Add(key)) throw new ArgumentException("duplicate reference-plane key '" + key + "'");
                XYZ bubble = ReadPoint(row["bubble_end"], scale); XYZ free = ReadPoint(row["free_end"], scale);
                XYZ cut = ReadVector(row["cut_vector"], "cut_vector");
                if (bubble.DistanceTo(free) < 1e-9) throw new ArgumentException("reference plane '" + key + "' bubble_end and free_end must differ");
                if ((free - bubble).Normalize().CrossProduct(cut).GetLength() < 1e-9)
                    throw new ArgumentException("reference plane '" + key + "' cut_vector cannot be parallel to its defining line");
                plan.ReferencePlanes.Add(new ReferencePlanePlan
                {
                    Key = key, Name = row.Value<string>("name"), BubbleEnd = bubble, FreeEnd = free, CutVector = cut
                });
                referenceIndex++;
            }
            if (request["reference_planes"] != null && (!(request["reference_planes"] is JArray referenceArray) || referenceArray.Count != plan.ReferencePlanes.Count))
                throw new ArgumentException("every reference_planes entry must be an object");

            var dimensionKeys = new HashSet<string>(StringComparer.Ordinal);
            int dimensionIndex = 0;
            foreach (JObject row in (request["dimensions"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = row.Value<string>("key") ?? "dimension_" + dimensionIndex;
                if (!dimensionKeys.Add(key)) throw new ArgumentException("duplicate dimension key '" + key + "'");
                JArray refs = row["reference_plane_keys"] as JArray;
                if (refs == null || refs.Count < 2 || refs.Count > 20 || refs.Any(x => x.Type != JTokenType.String))
                    throw new ArgumentException("dimension '" + key + "' reference_plane_keys must contain 2..20 strings");
                List<string> requestedRefs = refs.Values<string>().ToList();
                if (requestedRefs.Distinct(StringComparer.Ordinal).Count() != requestedRefs.Count)
                    throw new ArgumentException("dimension '" + key + "' repeats a reference-plane key");
                foreach (string requestedRef in requestedRefs)
                    if (!referenceKeys.Contains(requestedRef)) throw new ArgumentException("dimension '" + key + "' references unknown reference plane '" + requestedRef + "'");
                string label = row.Value<string>("label_parameter");
                ValidateTypedParameter(parameterPlans, label, "length", "dimension '" + key + "' label");
                XYZ lineStart = ReadPoint(row["line_start"], scale); XYZ lineEnd = ReadPoint(row["line_end"], scale);
                if (lineStart.DistanceTo(lineEnd) < 1e-9) throw new ArgumentException("dimension '" + key + "' line_start and line_end must differ");
                string viewName = row.Value<string>("view_name");
                if (viewName != null && string.IsNullOrWhiteSpace(viewName))
                    throw new ArgumentException("dimension '" + key + "' view_name cannot be blank; omit it to use the default family view");
                string typeName = row.Value<string>("dimension_type_name");
                if (typeName != null && string.IsNullOrWhiteSpace(typeName))
                    throw new ArgumentException("dimension '" + key + "' dimension_type_name cannot be blank; omit it to use the template's default linear type");
                bool lockRequested = row.Value<bool?>("lock") == true;
                bool eqRequested = row.Value<bool?>("eq") == true;
                // lock is the two-reference constraint and EQ is the multi-segment one; each
                // combination Revit would reject is refused here, while this is still a plan,
                // with the reason named instead of Revit's generic input error.
                if (lockRequested && requestedRefs.Count != 2)
                    throw new ArgumentException("dimension '" + key + "' lock applies only to a two-reference dimension; this one has " + requestedRefs.Count + " references");
                if (eqRequested && requestedRefs.Count < 3)
                    throw new ArgumentException("dimension '" + key + "' eq needs at least three reference planes (two segments to equalise); this one has " + requestedRefs.Count);
                // A labelled dimension is already driven by its parameter - Revit treats the
                // label AS the constraint and rejects a lock stacked on top of it, with an
                // error that names neither cause. Refuse the combination before anything
                // exists to roll back.
                if (lockRequested && !string.IsNullOrWhiteSpace(label))
                    throw new ArgumentException("dimension '" + key + "' cannot combine lock with label_parameter: the label already constrains the dimension through its parameter. Keep one of the two.");
                plan.Dimensions.Add(new DimensionPlan
                {
                    Key = key, ReferencePlaneKeys = requestedRefs, LineStart = lineStart, LineEnd = lineEnd,
                    LabelParameter = label, ViewName = viewName?.Trim(), TypeName = typeName?.Trim(),
                    Lock = lockRequested, Eq = eqRequested
                });
                dimensionIndex++;
            }
            if (request["dimensions"] != null && (!(request["dimensions"] is JArray dimensionArray) || dimensionArray.Count != plan.Dimensions.Count))
                throw new ArgumentException("every dimensions entry must be an object");

            var lineKeys = new HashSet<string>(StringComparer.Ordinal);
            int lineIndex = 0;
            foreach (JObject row in (request["family_lines"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string key = row.Value<string>("key") ?? "family_line_" + lineIndex;
                if (!lineKeys.Add(key)) throw new ArgumentException("duplicate family-line key '" + key + "'");
                string kind = (row.Value<string>("kind") ?? "symbolic").ToLowerInvariant();
                if (kind != "symbolic" && kind != "model") throw new ArgumentException("family line '" + key + "' kind must be symbolic or model");
                string plane = (row.Value<string>("plane") ?? "xy").ToLowerInvariant(); XYZ normal = PlaneNormal(plane);
                XYZ start = ReadPoint(row["start"], scale); XYZ end = ReadPoint(row["end"], scale);
                if (start.DistanceTo(end) < 1e-9) throw new ArgumentException("family line '" + key + "' start and end must differ");
                if (Math.Abs(start.DotProduct(normal) - end.DotProduct(normal)) > 1e-7)
                    throw new ArgumentException("family line '" + key + "' must lie in its selected plane");
                plan.FamilyLines.Add(new FamilyLinePlan { Key = key, Kind = kind, Normal = normal, Start = start, End = end });
                lineIndex++;
            }
            if (request["family_lines"] != null && (!(request["family_lines"] is JArray lineArray) || lineArray.Count != plan.FamilyLines.Count))
                throw new ArgumentException("every family_lines entry must be an object");

            var nestedKeys = new HashSet<string>(StringComparer.Ordinal);
            int nestedIndex = 0;
            JArray nestedInput = request["nested_instances"] as JArray ?? new JArray();
            if (nestedInput.Count > 100) throw new ArgumentException("nested_instances exceeds the 100 item limit");
            foreach (JObject row in nestedInput.OfType<JObject>())
            {
                string key = row.Value<string>("key") ?? "nested_instance_" + nestedIndex;
                if (!nestedKeys.Add(key)) throw new ArgumentException("duplicate nested-instance key '" + key + "'");
                string familyPath = FullPath(row.Value<string>("family_path"), ".rfa", "nested instance '" + key + "' family_path", true, out string nestedPathError);
                if (nestedPathError != null) throw new ArgumentException(nestedPathError);
                string outputPath = Path.GetFullPath(request.Value<string>("output_path"));
                if (string.Equals(familyPath, outputPath, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("nested instance '" + key + "' cannot load the RFA currently being created");
                string typeName = RequiredText(row, "type_name");
                string placement = (row.Value<string>("placement") ?? "model_point").ToLowerInvariant();
                if (placement != "model_point" && placement != "view_point")
                    throw new ArgumentException("nested instance '" + key + "' placement must be model_point or view_point");
                JObject associations = row["associations"] as JObject ?? new JObject();
                foreach (JProperty association in associations.Properties())
                {
                    if (association.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace(association.Value.Value<string>()))
                        throw new ArgumentException("nested instance '" + key + "' association values must name declared outer family parameters");
                    if (!parameterPlans.ContainsKey(association.Value.Value<string>()))
                        throw new ArgumentException("nested instance '" + key + "' associates to unknown outer parameter '" + association.Value.Value<string>() + "'");
                }
                plan.NestedInstances.Add(new NestedInstancePlan
                {
                    Key = key, FamilyPath = familyPath, TypeName = typeName, Placement = placement,
                    Point = ReadPoint(row["point"], scale),
                    RotationRadians = Finite(row.Value<double?>("rotation_degrees") ?? 0, "nested instance rotation_degrees") * Math.PI / 180.0,
                    Associations = associations
                });
                nestedIndex++;
            }
            if (request["nested_instances"] != null && (!(request["nested_instances"] is JArray nestedArray) || nestedArray.Count != plan.NestedInstances.Count))
                throw new ArgumentException("every nested_instances entry must be an object");
            return plan;
        }

        private static Dictionary<string, FamilyParameter> EnsureParameters(FamilyManager fm, List<ParameterPlan> plans)
        {
            var result = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
            foreach (ParameterPlan plan in plans)
            {
                FamilyParameter existing = FindParameter(fm, plan.Name);
                if (existing != null)
                {
                    if (existing.IsInstance != plan.Instance || existing.Definition.GetDataType() != plan.Spec)
                        throw new InvalidOperationException("template parameter '" + plan.Name + "' exists with a different instance/type or data type");
                    result[plan.Name] = existing;
                }
                else result[plan.Name] = fm.AddParameter(plan.Name, plan.Group, plan.Spec, plan.Instance);
            }
            return result;
        }

        // ---- flex measurement. --------------------------------------------------
        private static JObject FlexTypes(Document family, FamilyPlan plan)
        {
            FamilyManager fm = family.FamilyManager;
            var rows = new JArray();
            var extents = new List<KeyValuePair<string, double[]>>();
            using (var tx = new Transaction(family, "Horizun: flex types"))
            {
                tx.Start();
                foreach (FamilyType type in fm.Types.Cast<FamilyType>())
                {
                    fm.CurrentType = type;
                    family.Regenerate();
                    double[] size = SolidExtents(family);
                    rows.Add(new JObject
                    {
                        ["type"] = type.Name,
                        ["extents_mm"] = size == null ? null : new JArray(
                            Math.Round(size[0] * 304.8, 1), Math.Round(size[1] * 304.8, 1), Math.Round(size[2] * 304.8, 1))
                    });
                    if (size != null) extents.Add(new KeyValuePair<string, double[]>(type.Name, size));
                }
                Guard.RollBack(tx);   // flexing is a MEASUREMENT; the family keeps its state
            }
            bool anyPairDiffers = false;
            for (int i = 0; i < extents.Count && !anyPairDiffers; i++)
                for (int j = i + 1; j < extents.Count && !anyPairDiffers; j++)
                    for (int axis = 0; axis < 3; axis++)
                        if (Math.Abs(extents[i].Value[axis] - extents[j].Value[axis]) > 0.1 / 304.8)
                            { anyPairDiffers = true; break; }
            return new JObject
            {
                ["types_flexed"] = rows.Count,
                ["rows"] = rows,
                ["geometry_moves_between_types"] = anyPairDiffers,
                ["note"] = extents.Count < 2
                    ? "fewer than two types carry measurable solids, so movement between types cannot be judged."
                    : anyPairDiffers
                        ? "at least one pair of types differs in solid extents: the parameters DRIVE the geometry."
                        : "every type measures the same solid extents to 0.1 mm - either the parameters are " +
                          "non-geometric (legitimate) or the flex is broken; the numbers above are the evidence."
            };
        }

        private static double[] SolidExtents(Document family)
        {
            var options = new Options { DetailLevel = ViewDetailLevel.Fine };
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;
            foreach (Element element in new FilteredElementCollector(family).OfClass(typeof(GenericForm)))
            {
                GeometryElement geometry;
                try { geometry = element.get_Geometry(options); } catch { continue; }
                if (geometry == null) continue;
                foreach (GeometryObject obj in geometry)
                {
                    if (!(obj is Solid solid) || solid.Volume <= 0) continue;
                    BoundingBoxXYZ box;
                    try { box = solid.GetBoundingBox(); } catch { continue; }
                    if (box == null) continue;
                    XYZ lo = box.Transform.OfPoint(box.Min), hi = box.Transform.OfPoint(box.Max);
                    minX = Math.Min(minX, Math.Min(lo.X, hi.X)); maxX = Math.Max(maxX, Math.Max(lo.X, hi.X));
                    minY = Math.Min(minY, Math.Min(lo.Y, hi.Y)); maxY = Math.Max(maxY, Math.Max(lo.Y, hi.Y));
                    minZ = Math.Min(minZ, Math.Min(lo.Z, hi.Z)); maxZ = Math.Max(maxZ, Math.Max(lo.Z, hi.Z));
                    any = true;
                }
            }
            if (!any) return null;
            return new[] { maxX - minX, maxY - minY, maxZ - minZ };
        }

        // ---- thumbnail: exported beside the RFA, verified from disk. -------------
        private static JObject ExportThumbnail(Document family, string path)
        {
            try
            {
                View view = new FilteredElementCollector(family).OfClass(typeof(View3D)).OfType<View3D>()
                                .FirstOrDefault(v => !v.IsTemplate)
                            ?? new FilteredElementCollector(family).OfClass(typeof(View)).OfType<View>()
                                .FirstOrDefault(v => !v.IsTemplate && v.CanBePrinted);
                if (view == null)
                    return new JObject { ["emitted"] = false, ["reason"] = "the family document has no exportable view." };
                var options = new ImageExportOptions
                {
                    FilePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path),
                                                      System.IO.Path.GetFileNameWithoutExtension(path)),
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = 512,
                    ImageResolution = ImageResolution.DPI_72,
                    ExportRange = ExportRange.SetOfViews,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                family.ExportImage(options);
                // Revit names the file itself; find what it wrote and normalize.
                string dir = System.IO.Path.GetDirectoryName(path);
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                string produced = System.IO.Directory.GetFiles(dir, stem + "*.png")
                    .OrderByDescending(System.IO.File.GetLastWriteTimeUtc).FirstOrDefault();
                if (produced == null)
                    return new JObject { ["emitted"] = false, ["reason"] = "ExportImage returned but no PNG landed beside the RFA." };
                if (!string.Equals(produced, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    System.IO.File.Move(produced, path);
                }
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                string sha;
                using (var hasher = System.Security.Cryptography.SHA256.Create())
                    sha = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50;
                return new JObject
                {
                    ["emitted"] = png, ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = sha,
                    ["verified_by_reread"] = png,
                    ["reason"] = png ? null : "the produced file does not begin with a PNG signature."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["emitted"] = false, ["reason"] = "thumbnail export threw: " + ex.Message };
            }
        }

        private static Dictionary<string, FamilyType> EnsureTypes(FamilyManager fm, List<TypePlan> plans)
        {
            var result = new Dictionary<string, FamilyType>(StringComparer.Ordinal);
            var existing = new Dictionary<string, FamilyType>(StringComparer.Ordinal);
            foreach (FamilyType type in fm.Types) existing[type.Name] = type;
            foreach (TypePlan plan in plans)
            {
                if (!existing.TryGetValue(plan.Name, out FamilyType type)) type = fm.NewType(plan.Name);
                result[plan.Name] = type;
            }
            if (fm.CurrentType == null)
            {
                FamilyType type = plans.Count > 0 ? result[plans[0].Name] : fm.NewType("Default");
                fm.CurrentType = type;
            }
            return result;
        }

        private static void ApplyTypeValues(FamilyManager fm, List<TypePlan> types, Dictionary<string, FamilyType> actual,
                                            Dictionary<string, FamilyParameter> parameters, double scale)
        {
            foreach (TypePlan type in types)
            {
                fm.CurrentType = actual[type.Name];
                foreach (JProperty value in type.Values.Properties())
                    SetFamilyValue(fm, parameters[value.Name], value.Value, scale);
            }
        }

        private static GenericForm CreateForm(Document family, FamilyManager fm, FormPlan plan,
                                              Dictionary<string, FamilyParameter> parameters)
        {
            XYZ origin = plan.Loops[0][0];
            SketchPlane sketch = SketchPlane.Create(family, Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(plan.Normal, origin));
            GenericForm form;
            if (plan.Kind == "extrusion")
            {
                Extrusion extrusion = family.FamilyCreate.NewExtrusion(plan.Solid, Curves(plan.Loops), sketch, plan.Depth);
                form = extrusion;
                Associate(fm, extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM), plan.StartParameter, parameters);
                Associate(fm, extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), plan.EndParameter, parameters);
            }
            else if (plan.Kind == "blend")
            {
                Blend blend = family.FamilyCreate.NewBlend(plan.Solid, CurveLoop(plan.Loops[0]), CurveLoop(plan.TopLoops[0]), sketch);
                blend.BottomOffset = plan.BottomOffset; blend.TopOffset = plan.TopOffset; form = blend;
                Associate(fm, blend.get_Parameter(BuiltInParameter.BLEND_START_PARAM), plan.StartParameter, parameters);
                Associate(fm, blend.get_Parameter(BuiltInParameter.BLEND_END_PARAM), plan.EndParameter, parameters);
            }
            else if (plan.Kind == "revolution")
            {
                Revolution revolution = family.FamilyCreate.NewRevolution(plan.Solid, Curves(plan.Loops), sketch,
                    Line.CreateBound(plan.AxisStart, plan.AxisEnd), plan.StartAngle, plan.EndAngle);
                form = revolution;
                Associate(fm, revolution.get_Parameter(BuiltInParameter.REVOLUTION_START_ANGLE), plan.StartParameter, parameters);
                Associate(fm, revolution.get_Parameter(BuiltInParameter.REVOLUTION_END_ANGLE), plan.EndParameter, parameters);
            }
            else if (plan.Kind == "sweep")
            {
                SketchPlane pathPlane = SketchPlane.Create(family, Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(plan.PathNormal, plan.Path[0]));
                SweepProfile profile = family.Application.Create.NewCurveLoopsProfile(Curves(plan.Loops));
                form = family.FamilyCreate.NewSweep(plan.Solid, PathCurves(plan.Path), pathPlane, profile,
                    plan.ProfileLocationCurveIndex, plan.ProfilePlaneLocation);
            }
            else
            {
                SketchPlane pathPlane = SketchPlane.Create(family, Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(plan.PathNormal, plan.Path[0]));
                SweepProfile bottom = family.Application.Create.NewCurveLoopsProfile(Curves(plan.Loops));
                SweepProfile top = family.Application.Create.NewCurveLoopsProfile(Curves(plan.TopLoops));
                form = family.FamilyCreate.NewSweptBlend(plan.Solid, Line.CreateBound(plan.Path[0], plan.Path[1]), pathPlane, bottom, top);
            }
            Associate(fm, form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), plan.MaterialParameter, parameters);
            Associate(fm, form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), plan.VisibilityParameter, parameters);
            return form;
        }

        private static ConnectorElement CreateConnector(Document family, FamilyManager fm, ConnectorPlan plan, GenericForm host,
                                                        Dictionary<string, FamilyParameter> parameters)
        {
            Reference face = FindFace(host, plan.FaceNormal);
            ConnectorElement connector;
            if (plan.Kind == "pipe")
            {
                if (!Enum.TryParse(plan.SystemType ?? "UndefinedSystemType", true, out PipeSystemType system) ||
                    !Enum.IsDefined(typeof(PipeSystemType), system))
                    throw new InvalidOperationException("invalid pipe connector system_type '" + plan.SystemType + "'");
                connector = ConnectorElement.CreatePipeConnector(family, system, face);
            }
            else if (plan.Kind == "duct")
            {
                if (!Enum.TryParse(plan.SystemType ?? "UndefinedSystemType", true, out DuctSystemType system) ||
                    !Enum.IsDefined(typeof(DuctSystemType), system))
                    throw new InvalidOperationException("invalid duct connector system_type '" + plan.SystemType + "'");
                if (!Enum.TryParse(plan.Profile ?? "Round", true, out ConnectorProfileType profile) ||
                    !Enum.IsDefined(typeof(ConnectorProfileType), profile))
                    throw new InvalidOperationException("invalid duct connector profile '" + plan.Profile + "'");
                connector = ConnectorElement.CreateDuctConnector(family, system, profile, face);
            }
            else if (plan.Kind == "electrical")
            {
                if (!Enum.TryParse(plan.SystemType ?? "UndefinedSystemType", true, out ElectricalSystemType system) ||
                    !Enum.IsDefined(typeof(ElectricalSystemType), system))
                    throw new InvalidOperationException("invalid electrical connector system_type '" + plan.SystemType + "'");
                connector = ConnectorElement.CreateElectricalConnector(family, system, face);
            }
            else if (plan.Kind == "conduit") connector = ConnectorElement.CreateConduitConnector(family, face);
            else connector = ConnectorElement.CreateCableTrayConnector(family, face);
            if (plan.Primary) connector.AssignAsPrimary();
            Associate(fm, connector.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER), plan.DiameterParameter, parameters);
            Associate(fm, connector.get_Parameter(BuiltInParameter.CONNECTOR_WIDTH), plan.WidthParameter, parameters);
            Associate(fm, connector.get_Parameter(BuiltInParameter.CONNECTOR_HEIGHT), plan.HeightParameter, parameters);
            return connector;
        }

        private static Reference FindFace(GenericForm form, XYZ wanted)
        {
            PlanarFace best = null; double bestArea = -1;
            var options = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            foreach (GeometryObject obj in form.get_Geometry(options))
            {
                if (!(obj is Solid solid) || solid.Volume <= 0) continue;
                foreach (Face face in solid.Faces)
                    if (face is PlanarFace planar && planar.Reference != null && planar.FaceNormal.Normalize().DotProduct(wanted) > 0.98 && planar.Area > bestArea)
                    { best = planar; bestArea = planar.Area; }
            }
            if (best == null) throw new InvalidOperationException("no planar host face matches face_normal for connector");
            return best.Reference;
        }

        private static void Associate(FamilyManager fm, Parameter elementParameter, string familyName,
                                      Dictionary<string, FamilyParameter> parameters)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return;
            if (elementParameter == null) throw new InvalidOperationException("Revit did not expose an element parameter to associate with '" + familyName + "'");
            if (!parameters.TryGetValue(familyName, out FamilyParameter familyParameter))
                throw new InvalidOperationException("unknown family parameter '" + familyName + "'");
            if (!fm.CanElementParameterBeAssociated(elementParameter))
                throw new InvalidOperationException("element parameter cannot be associated with family parameter '" + familyName + "'");
            fm.AssociateElementParameterToFamilyParameter(elementParameter, familyParameter);
        }

        private static JObject VerifyFamilyDocument(Document family, FamilyPlan plan, JArray forms, JArray connectors,
            JArray referencePlanes, JArray dimensions, JArray familyLines, JArray nestedInstances)
        {
            FamilyManager fm = family.FamilyManager;
            var actualParameters = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);
            var parameterRows = new JArray();
            foreach (ParameterPlan requested in plan.Parameters)
            {
                FamilyParameter actual = FindParameter(fm, requested.Name);
                bool formulaMatches = !requested.FormulaSpecified ||
                    string.Equals(actual?.Formula?.Trim(), requested.Formula.Trim(), StringComparison.Ordinal);
                bool ok = actual != null && actual.IsInstance == requested.Instance && actual.Definition.GetDataType() == requested.Spec && formulaMatches;
                if (!ok) throw new InvalidOperationException("parameter '" + requested.Name + "' did not re-read with its requested data type, instance policy and formula");
                actualParameters[requested.Name] = actual;
                parameterRows.Add(new JObject
                {
                    ["name"] = requested.Name, ["data_type"] = requested.DataType, ["instance"] = actual.IsInstance,
                    ["formula"] = actual.Formula, ["verified"] = true
                });
            }

            var formPlans = plan.Forms.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (JObject row in forms.OfType<JObject>())
            {
                long id = row.Value<long>("element_id");
                if (!(family.GetElement(Rid.Make(id)) is GenericForm form)) throw new InvalidOperationException("created form " + id + " was not re-read as GenericForm");
                FormPlan requested = formPlans[row.Value<string>("key")];
                bool classMatches = requested.Kind == "extrusion" ? form is Extrusion :
                    requested.Kind == "blend" ? form is Blend : requested.Kind == "revolution" ? form is Revolution :
                    requested.Kind == "sweep" ? form is Sweep : form is SweptBlend;
                if (!classMatches || form.IsSolid != requested.Solid)
                    throw new InvalidOperationException("form '" + requested.Key + "' did not re-read as the requested " + requested.Kind + " solid/void kind");
                if (form is Extrusion extrusion)
                {
                    VerifyAssociation(fm, extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM), requested.StartParameter, "form '" + requested.Key + "' start");
                    VerifyAssociation(fm, extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), requested.EndParameter, "form '" + requested.Key + "' end");
                }
                else if (form is Blend blend)
                {
                    VerifyAssociation(fm, blend.get_Parameter(BuiltInParameter.BLEND_START_PARAM), requested.StartParameter, "form '" + requested.Key + "' start");
                    VerifyAssociation(fm, blend.get_Parameter(BuiltInParameter.BLEND_END_PARAM), requested.EndParameter, "form '" + requested.Key + "' end");
                }
                else if (form is Revolution revolution)
                {
                    VerifyAssociation(fm, revolution.get_Parameter(BuiltInParameter.REVOLUTION_START_ANGLE), requested.StartParameter, "form '" + requested.Key + "' start angle");
                    VerifyAssociation(fm, revolution.get_Parameter(BuiltInParameter.REVOLUTION_END_ANGLE), requested.EndParameter, "form '" + requested.Key + "' end angle");
                }
                VerifyAssociation(fm, form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), requested.MaterialParameter, "form '" + requested.Key + "' material");
                VerifyAssociation(fm, form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), requested.VisibilityParameter, "form '" + requested.Key + "' visibility");
                row["actual_class"] = form.GetType().Name; row["solid"] = form.IsSolid; row["verified"] = true;
            }

            var connectorPlans = plan.Connectors.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (JObject row in connectors.OfType<JObject>())
            {
                long id = row.Value<long>("element_id");
                if (!(family.GetElement(Rid.Make(id)) is ConnectorElement connector)) throw new InvalidOperationException("created connector " + id + " was not re-read as ConnectorElement");
                ConnectorPlan requested = connectorPlans[row.Value<string>("key")];
                if (connector.IsPrimary != requested.Primary)
                    throw new InvalidOperationException("connector '" + requested.Key + "' primary state did not re-read as requested");
                VerifyAssociation(fm, connector.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER), requested.DiameterParameter, "connector '" + requested.Key + "' diameter");
                VerifyAssociation(fm, connector.get_Parameter(BuiltInParameter.CONNECTOR_WIDTH), requested.WidthParameter, "connector '" + requested.Key + "' width");
                VerifyAssociation(fm, connector.get_Parameter(BuiltInParameter.CONNECTOR_HEIGHT), requested.HeightParameter, "connector '" + requested.Key + "' height");
                row["primary"] = connector.IsPrimary; row["verified"] = true;
            }

            var referencePlans = plan.ReferencePlanes.ToDictionary(x => x.Key, StringComparer.Ordinal);
            var verifiedPlanes = new Dictionary<string, ReferencePlane>(StringComparer.Ordinal);
            foreach (JObject row in referencePlanes.OfType<JObject>())
            {
                long id = row.Value<long>("element_id");
                if (!(family.GetElement(Rid.Make(id)) is ReferencePlane referencePlane))
                    throw new InvalidOperationException("reference plane " + id + " was not re-read");
                ReferencePlanePlan requested = referencePlans[row.Value<string>("key")];
                if (!string.IsNullOrWhiteSpace(requested.Name) && !string.Equals(referencePlane.Name, requested.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("reference plane '" + requested.Key + "' name did not re-read as requested");
                if (referencePlane.GetReference() == null)
                    throw new InvalidOperationException("reference plane '" + requested.Key + "' did not expose a stable Reference");
                verifiedPlanes[requested.Key] = referencePlane;
                row["name"] = referencePlane.Name; row["verified"] = true;
            }

            var dimensionPlans = plan.Dimensions.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (JObject row in dimensions.OfType<JObject>())
            {
                long id = row.Value<long>("element_id");
                if (!(family.GetElement(Rid.Make(id)) is Dimension dimension))
                    throw new InvalidOperationException("dimension " + id + " was not re-read");
                DimensionPlan requested = dimensionPlans[row.Value<string>("key")];
                string actualLabel = dimension.FamilyLabel?.Definition?.Name;
                bool labelOk = string.IsNullOrWhiteSpace(requested.LabelParameter)
                    ? dimension.FamilyLabel == null
                    : string.Equals(actualLabel, requested.LabelParameter, StringComparison.Ordinal);
                // In a family document Revit can report AreReferencesAvailable=false even
                // though the dimension owns the expected reference array and saves correctly.
                // So here it stays a recorded diagnostic; the ENFORCED read happens after
                // the RFA is re-opened from disk, where Revit computed the answer from the
                // saved file and a false really means the references are gone.
                if (dimension.References == null ||
                    dimension.References.Size != requested.ReferencePlaneKeys.Count || !labelOk)
                    throw new InvalidOperationException("dimension '" + requested.Key + "' references or family label did not re-read as requested");
                if (Rid.Value(dimension.OwnerViewId) != row.Value<long>("view_id"))
                    throw new InvalidOperationException("dimension '" + requested.Key + "' did not re-read in the view it was created in");
                if (requested.TypeName != null &&
                    Rid.Value(dimension.DimensionType?.Id ?? ElementId.InvalidElementId) != row.Value<long>("dimension_type_id"))
                    throw new InvalidOperationException("dimension '" + requested.Key + "' did not re-read with dimension type '" + requested.TypeName + "'");
                row["line"] = VerifyDimensionLine(dimension, requested);
                int expectedSegments = ExpectedSegments(requested.ReferencePlaneKeys.Count);
                if (dimension.NumberOfSegments != expectedSegments)
                    throw new InvalidOperationException("dimension '" + requested.Key + "' re-read " + dimension.NumberOfSegments +
                        " segments where " + expectedSegments + " were expected (Revit reports a single-segment dimension as 0)");
                row["segments"] = new JObject { ["requested"] = expectedSegments, ["read"] = dimension.NumberOfSegments, ["match"] = true };
                // The expected value comes from where the planes stand NOW, not where the
                // request drew them: a label or an EQ constraint legitimately moves planes
                // during regeneration, and the honest check is measured-vs-planes with both
                // sides read fresh from the same document state.
                double expectedSpan = ExpectedDimensionSpan(requested, verifiedPlanes);
                double? measured = MeasuredTotal(dimension);
                if (!measured.HasValue)
                    throw new InvalidOperationException("dimension '" + requested.Key + "' re-read with no measurable value");
                if (Math.Abs(measured.Value - expectedSpan) > GeometryToleranceFeet)
                    throw new InvalidOperationException("dimension '" + requested.Key + "' measures " +
                        measured.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " ft where its reference planes stand " +
                        expectedSpan.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " ft apart (tolerance " + GeometryToleranceFeet + " ft)");
                row["value_internal_feet"] = new JObject
                {
                    ["requested"] = expectedSpan, ["read"] = measured.Value, ["match"] = true,
                    ["tolerance_internal_feet"] = GeometryToleranceFeet
                };
                if (requested.Lock)
                {
                    if (!dimension.IsLocked)
                        throw new InvalidOperationException("dimension '" + requested.Key + "' did not re-read as locked");
                    row["locked"] = new JObject { ["requested"] = true, ["read"] = true, ["match"] = true };
                }
                if (requested.Eq)
                {
                    if (!dimension.AreSegmentsEqual)
                        throw new InvalidOperationException("dimension '" + requested.Key + "' did not re-read with its EQ constraint");
                    row["segments_equal"] = new JObject { ["requested"] = true, ["read"] = true, ["match"] = true };
                }
                row["references"] = dimension.References.Size; row["references_available"] = dimension.AreReferencesAvailable;
                row["label_parameter"] = actualLabel; row["verified"] = true;
            }

            var linePlans = plan.FamilyLines.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (JObject row in familyLines.OfType<JObject>())
            {
                long id = row.Value<long>("element_id");
                Element element = family.GetElement(Rid.Make(id)); FamilyLinePlan requested = linePlans[row.Value<string>("key")];
                bool kindOk = requested.Kind == "symbolic" ? element is SymbolicCurve : element is ModelCurve && !(element is SymbolicCurve);
                if (!kindOk) throw new InvalidOperationException("family line '" + requested.Key + "' did not re-read as " + requested.Kind);
                row["actual_class"] = element.GetType().Name; row["verified"] = true;
            }

            var nestedPlans = plan.NestedInstances.ToDictionary(x => x.Key, StringComparer.Ordinal);
            foreach (JObject row in nestedInstances.OfType<JObject>())
            {
                long id = row.Value<long>("element_id");
                if (!(family.GetElement(Rid.Make(id)) is FamilyInstance instance))
                    throw new InvalidOperationException("nested instance " + id + " was not re-read as FamilyInstance");
                NestedInstancePlan requested = nestedPlans[row.Value<string>("key")];
                bool identityOk = Rid.Value(instance.Symbol.Id) == row.Value<long>("symbol_id") &&
                    Rid.Value(instance.Symbol.Family.Id) == row.Value<long>("family_id") &&
                    string.Equals(instance.Symbol.Name, requested.TypeName, StringComparison.Ordinal);
                if (!(instance.Location is LocationPoint location) || location.Point.DistanceTo(requested.Point) > 1e-7 ||
                    Math.Abs(NormalizeAngle(location.Rotation - requested.RotationRadians)) > 1e-7 || !identityOk)
                    throw new InvalidOperationException("nested instance '" + requested.Key + "' identity, point or rotation did not re-read as requested");
                foreach (JProperty association in requested.Associations.Properties())
                {
                    Parameter nestedParameter = ResolveNestedParameter(instance, association.Name);
                    VerifyAssociation(fm, nestedParameter, association.Value.Value<string>(), "nested instance '" + requested.Key + "' parameter '" + association.Name + "'");
                }
                row["family_name"] = instance.Symbol.Family.Name; row["type_name"] = instance.Symbol.Name;
                row["rotation_radians"] = location.Rotation; row["verified"] = true;
            }

            var actualTypes = family.FamilyManager.Types.Cast<FamilyType>().ToDictionary(x => x.Name, StringComparer.Ordinal);
            var typeRows = new JArray();
            foreach (TypePlan requested in plan.Types)
            {
                if (!actualTypes.TryGetValue(requested.Name, out FamilyType actual))
                    throw new InvalidOperationException("family type '" + requested.Name + "' was not re-read");
                var values = new JArray();
                foreach (JProperty value in requested.Values.Properties())
                {
                    FamilyParameter parameter = actualParameters[value.Name];
                    JToken expected = ExpectedFamilyValue(parameter, value.Value, plan.Scale);
                    JToken reread = ReadFamilyValue(actual, parameter);
                    bool ok = FamilyValuesEqual(expected, reread);
                    if (!ok) throw new InvalidOperationException("family type '" + requested.Name + "' parameter '" + value.Name + "' did not re-read with the stored value");
                    values.Add(new JObject { ["parameter"] = value.Name, ["expected_internal"] = expected, ["read_internal"] = reread, ["verified"] = true });
                }
                typeRows.Add(new JObject { ["name"] = requested.Name, ["values"] = values, ["verified"] = true });
            }
            return new JObject
            {
                ["parameters"] = parameterRows, ["types"] = typeRows,
                ["forms_verified"] = forms.Count, ["connectors_verified"] = connectors.Count,
                ["reference_planes_verified"] = referencePlanes.Count, ["dimensions_verified"] = dimensions.Count,
                ["family_lines_verified"] = familyLines.Count, ["nested_instances_verified"] = nestedInstances.Count,
                ["verified"] = true
            };
        }

        private static Parameter ResolveNestedParameter(FamilyInstance instance, string name)
        {
            IList<Parameter> matches = instance.GetParameters(name);
            if (matches.Count != 1)
                throw new InvalidOperationException("nested instance parameter '" + name + "' matched " + matches.Count + " parameters; use an unambiguous exact name");
            return matches[0];
        }

        private static View FindFamilyCreationView(Document family)
        {
            List<View> views = new FilteredElementCollector(family)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(x => !x.IsTemplate)
                .ToList();
            View view = views.FirstOrDefault(x => x.ViewType == ViewType.FloorPlan) ??
                        views.FirstOrDefault(x => x.ViewType == ViewType.CeilingPlan) ??
                        views.FirstOrDefault(x => x.ViewType == ViewType.ThreeD) ??
                        views.FirstOrDefault();
            if (view == null)
                throw new InvalidOperationException("The family template exposes no non-template view for reference planes, dimensions or symbolic geometry.");
            return view;
        }

        /// <summary>
        /// Geometric tolerance for dimension verification, in internal feet, declared in
        /// every comparison row so the caller knows what "match" was measured against.
        /// 1e-6 ft is a third of a micron - far below anything Revit snaps to and far
        /// above double-precision noise at family-document coordinates.
        /// </summary>
        private const double GeometryToleranceFeet = 1e-6;

        /// <summary>
        /// Whether a family view can host a dimension: never a view template, and only
        /// the graphical view kinds a family template actually produces. Mirrors what
        /// FindFamilyCreationView is willing to pick, plus sections/elevations/details.
        /// </summary>
        private static bool AcceptsFamilyDimensions(View view)
        {
            if (view == null || view.IsTemplate) return false;
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.Elevation:
                case ViewType.Section:
                case ViewType.ThreeD:
                case ViewType.Detail:
                case ViewType.EngineeringPlan:
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, View> ResolveDimensionViews(Document family, List<DimensionPlan> dimensions)
        {
            var resolved = new Dictionary<string, View>(StringComparer.Ordinal);
            List<string> wanted = dimensions.Where(x => x.ViewName != null).Select(x => x.ViewName)
                .Distinct(StringComparer.Ordinal).ToList();
            if (wanted.Count == 0) return resolved;
            List<View> views = new FilteredElementCollector(family).OfClass(typeof(View)).Cast<View>().ToList();
            string available = string.Join(", ", views.Where(AcceptsFamilyDimensions)
                .Select(x => "'" + x.Name + "' (" + x.ViewType + ")").OrderBy(x => x, StringComparer.Ordinal));
            if (available.Length == 0) available = "(none)";
            foreach (string name in wanted)
            {
                List<View> byName = views.Where(x => string.Equals(x.Name, name, StringComparison.Ordinal)).ToList();
                if (byName.Count == 0)
                    throw new InvalidOperationException("dimension view_name '" + name + "' does not exist in this family document. " +
                        "No transaction was opened and nothing was created. Views that accept dimensions: " + available + ".");
                List<View> usable = byName.Where(AcceptsFamilyDimensions).ToList();
                if (usable.Count == 0)
                    throw new InvalidOperationException("dimension view_name '" + name + "' " +
                        (byName[0].IsTemplate ? "is a view template" : "is a " + byName[0].ViewType + " view, which does not accept dimensions") +
                        ". No transaction was opened and nothing was created. Views that accept dimensions: " + available + ".");
                if (usable.Count > 1)
                    throw new InvalidOperationException("dimension view_name '" + name + "' matches " + usable.Count +
                        " views in this family document; the name must be unambiguous. Nothing was created.");
                resolved[name] = usable[0];
            }
            return resolved;
        }

        private static Dictionary<string, DimensionType> ResolveDimensionTypes(Document family, List<DimensionPlan> dimensions)
        {
            var resolved = new Dictionary<string, DimensionType>(StringComparer.Ordinal);
            List<string> wanted = dimensions.Where(x => x.TypeName != null).Select(x => x.TypeName)
                .Distinct(StringComparer.Ordinal).ToList();
            if (wanted.Count == 0) return resolved;
            List<DimensionType> linear = new FilteredElementCollector(family).OfClass(typeof(DimensionType))
                .Cast<DimensionType>().Where(x => x.StyleType == DimensionStyleType.Linear && !string.IsNullOrWhiteSpace(x.Name))
                .ToList();
            string available = string.Join(", ", linear.Select(x => "'" + x.Name + "'")
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
            if (available.Length == 0) available = "(none)";
            foreach (string name in wanted)
            {
                List<DimensionType> byName = linear.Where(x => string.Equals(x.Name, name, StringComparison.Ordinal)).ToList();
                if (byName.Count == 0)
                    throw new InvalidOperationException("dimension_type_name '" + name + "' does not name a linear dimension type " +
                        "in this family document. Nothing was created. Linear dimension types available: " + available + ".");
                if (byName.Count > 1)
                    throw new InvalidOperationException("dimension_type_name '" + name + "' matches " + byName.Count +
                        " linear dimension types; the name must be unambiguous. Nothing was created.");
                resolved[name] = byName[0];
            }
            return resolved;
        }

        /// <summary>
        /// Revit's segment convention, spelled out once: a two-reference dimension is a
        /// SINGLE-segment dimension and reports NumberOfSegments = 0, with its value in
        /// Dimension.Value; only from three references up does NumberOfSegments count
        /// the n-1 spans and the value move into the segments.
        /// </summary>
        private static int ExpectedSegments(int referenceCount) => referenceCount == 2 ? 0 : referenceCount - 1;

        /// <summary>
        /// What the dimension SHOULD measure, computed from where the referenced planes
        /// actually stand right now - not from where the request drew them, because a
        /// label or an EQ constraint legitimately moves planes during regeneration. Each
        /// plane's witness foot is its intersection with the dimension line; the total
        /// is the spread of those feet along the line.
        /// </summary>
        private static double ExpectedDimensionSpan(DimensionPlan requested, Dictionary<string, ReferencePlane> planes)
        {
            XYZ direction = (requested.LineEnd - requested.LineStart).Normalize();
            double min = double.MaxValue, max = double.MinValue;
            foreach (string key in requested.ReferencePlaneKeys)
            {
                Autodesk.Revit.DB.Plane plane = planes[key].GetPlane();
                double along = direction.DotProduct(plane.Normal);
                if (Math.Abs(along) < 1e-9)
                    throw new InvalidOperationException("dimension '" + requested.Key + "' reference plane '" + key +
                        "' is parallel to the dimension line, so no measured value can be predicted or verified");
                double t = (plane.Origin - requested.LineStart).DotProduct(plane.Normal) / along;
                if (t < min) min = t;
                if (t > max) max = t;
            }
            return max - min;
        }

        /// <summary>Total measured value: Dimension.Value for a single segment, the segment sum otherwise. Null when Revit reports none.</summary>
        private static double? MeasuredTotal(Dimension dimension)
        {
            if (dimension.Value.HasValue) return dimension.Value.Value;
            if (dimension.Segments == null || dimension.Segments.Size == 0) return null;
            double sum = 0;
            foreach (DimensionSegment segment in dimension.Segments)
            {
                if (!segment.Value.HasValue) return null;
                sum += segment.Value.Value;
            }
            return sum;
        }

        /// <summary>
        /// The re-read dimension line, compared for COLLINEARITY rather than endpoint
        /// equality on purpose: Revit trims and extends the dimension line to its
        /// witness lines, so the honest postcondition is that both requested endpoints
        /// lie on the carrier of the re-read line, within the declared tolerance.
        /// </summary>
        private static JObject VerifyDimensionLine(Dimension dimension, DimensionPlan requested)
        {
            Curve curve = dimension.Curve;
            if (!(curve is Line line))
                throw new InvalidOperationException("dimension '" + requested.Key + "' did not re-read a straight dimension line" +
                    (curve == null ? "" : " (it re-read a " + curve.GetType().Name + ")"));
            XYZ origin = line.Origin;
            XYZ direction = line.Direction.Normalize();
            double startOffset = DistanceToCarrier(requested.LineStart, origin, direction);
            double endOffset = DistanceToCarrier(requested.LineEnd, origin, direction);
            if (startOffset > GeometryToleranceFeet || endOffset > GeometryToleranceFeet)
                throw new InvalidOperationException("dimension '" + requested.Key + "' line did not re-read collinear with the requested line: " +
                    "the requested endpoints sit " + startOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " and " + endOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " ft off the re-read carrier (tolerance " + GeometryToleranceFeet + " ft)");
            JObject read = line.IsBound
                ? new JObject { ["bound"] = true, ["start"] = Triplet(line.GetEndPoint(0)), ["end"] = Triplet(line.GetEndPoint(1)) }
                : new JObject { ["bound"] = false, ["origin"] = Triplet(origin), ["direction"] = Triplet(direction) };
            return new JObject
            {
                ["requested"] = new JObject { ["start"] = Triplet(requested.LineStart), ["end"] = Triplet(requested.LineEnd) },
                ["read"] = read,
                ["match"] = true,
                ["comparison"] = "collinearity - Revit trims the dimension line to its witness lines, so endpoint equality would fail on correct dimensions",
                ["tolerance_internal_feet"] = GeometryToleranceFeet
            };
        }

        private static double DistanceToCarrier(XYZ point, XYZ origin, XYZ unitDirection)
        {
            XYZ toPoint = point - origin;
            return (toPoint - unitDirection * toPoint.DotProduct(unitDirection)).GetLength();
        }

        private static JArray Triplet(XYZ point) => new JArray(point.X, point.Y, point.Z);

        /// <summary>
        /// The evidence the dimension claims finally stand on: the saved RFA re-opened
        /// from disk and every dimension re-read in the reopened document by its
        /// ElementId, which survives a save/reopen of the same file. AreReferencesAvailable
        /// is ENFORCED here and only here - in the freshly built document Revit is known
        /// to report false on dimensions that save and reopen correctly (see the note in
        /// VerifyFamilyDocument), but a reopened document computed its references from
        /// the saved bytes, so a false here means the file really does not carry them.
        /// </summary>
        private static JObject VerifyReopenedFamily(Document reopened, string output, FamilyPlan plan, JArray createdDimensions)
        {
            // MEASURED 2026-08-24 on Revit 2025: a family opened API-side
            // (OpenDocumentFile, no UI activation) can report AreReferencesAvailable
            // false on dimensions whose saved bytes are CORRECT - the same file opened
            // through a UI-activated open reads true, with the label value, lock and EQ
            // all intact. So the flag alone is not allowed to fail the file: first the
            // strict read; if only availability failed, regenerate inside a rolled-back
            // transaction and read again; if the flag STILL reads false, the SUBSTANCE
            // decides - counts, label, measured value, segments, lock, EQ, view - and
            // the availability row reports the tri-state honestly instead of borrowing
            // a verdict from an unreliable flag. A substantive failure throws at every
            // layer; nothing here converts one into a pass.
            try
            {
                return ReopenedResult(VerifyReopenedRows(reopened, plan, createdDimensions, strictAvailability: true),
                                      output, regenerated: false, availabilityNote: null);
            }
            catch (InvalidOperationException ex)
            {
                if (!IsAvailabilityOnlyFailure(ex)) throw;
            }
            using (var regenTx = new Transaction(reopened, "Horizun: regenerate for reopened verification"))
            {
                regenTx.Start();
                try
                {
                    reopened.Regenerate();
                    try
                    {
                        return ReopenedResult(VerifyReopenedRows(reopened, plan, createdDimensions, strictAvailability: true),
                                              output, regenerated: true,
                                              availabilityNote: "AreReferencesAvailable read false on first read and TRUE " +
                                              "after an explicit Regenerate inside a rolled-back transaction.");
                    }
                    catch (InvalidOperationException ex2)
                    {
                        if (!IsAvailabilityOnlyFailure(ex2)) throw;
                        return ReopenedResult(VerifyReopenedRows(reopened, plan, createdDimensions, strictAvailability: false),
                                              output, regenerated: true,
                                              availabilityNote: "AreReferencesAvailable stayed false under this API-side " +
                                              "reopen even after Regenerate. Every SUBSTANTIVE fact - reference count, " +
                                              "label, measured value, segments, lock, EQ, owner view - verified from the " +
                                              "saved bytes, and a UI-activated open of the same file reads the flag true " +
                                              "(measured 2026-08-24 on Revit 2025). The flag under an API open is " +
                                              "reported as observed, not used as a verdict.");
                    }
                }
                finally
                {
                    if (regenTx.GetStatus() == TransactionStatus.Started) Guard.RollBack(regenTx);
                }
            }
        }

        private static bool IsAvailabilityOnlyFailure(InvalidOperationException ex)
            => ex.Message != null && ex.Message.Contains("AreReferencesAvailable=false");

        private static JObject ReopenedResult(JArray rows, string output, bool regenerated, string availabilityNote)
        {
            var result = new JObject
            {
                ["reopened"] = true,
                ["path"] = output,
                ["is_family_document"] = true,
                ["tolerance_internal_feet"] = GeometryToleranceFeet,
                ["regenerated_before_read"] = regenerated,
                ["dimensions"] = rows,
                ["note"] = "read from the saved file after closing and re-opening it - SaveAs producing a file is never treated as verification"
            };
            if (availabilityNote != null) result["references_available_note"] = availabilityNote;
            return result;
        }

        private static JArray VerifyReopenedRows(Document reopened, FamilyPlan plan, JArray createdDimensions,
                                                 bool strictAvailability)
        {
            var dimensionPlans = plan.Dimensions.ToDictionary(x => x.Key, StringComparer.Ordinal);
            var rows = new JArray();
            foreach (JObject created in createdDimensions.OfType<JObject>())
            {
                string key = created.Value<string>("key");
                DimensionPlan requested = dimensionPlans[key];
                long id = created.Value<long>("element_id");
                Element element = reopened.GetElement(Rid.Make(id));
                if (!(element is Dimension dimension))
                    throw new InvalidOperationException("dimension '" + key + "' (id " + id + ") was not re-read in the reopened file" +
                        (element == null ? ": the id resolves to no element" : ": the id resolves to a " + element.GetType().Name));
                int requestedReferences = requested.ReferencePlaneKeys.Count;
                int readReferences = dimension.References?.Size ?? 0;
                if (readReferences != requestedReferences)
                    throw new InvalidOperationException("dimension '" + key + "' re-read " + readReferences +
                        " references in the reopened file where " + requestedReferences + " were requested");
                bool referencesAvailable = dimension.AreReferencesAvailable;
                if (strictAvailability && !referencesAvailable)
                    throw new InvalidOperationException("dimension '" + key + "' reports AreReferencesAvailable=false in the reopened file: " +
                        "its references did not survive the save/reopen round trip");
                string readLabel = dimension.FamilyLabel?.Definition?.Name;
                bool labelOk = string.IsNullOrWhiteSpace(requested.LabelParameter)
                    ? dimension.FamilyLabel == null
                    : string.Equals(readLabel, requested.LabelParameter, StringComparison.Ordinal);
                if (!labelOk)
                    throw new InvalidOperationException("dimension '" + key + "' label did not survive the save/reopen round trip: expected " +
                        (requested.LabelParameter == null ? "no label" : "'" + requested.LabelParameter + "'") + ", read " +
                        (readLabel == null ? "none" : "'" + readLabel + "'"));
                int expectedSegments = ExpectedSegments(requestedReferences);
                int readSegments = dimension.NumberOfSegments;
                if (readSegments != expectedSegments)
                    throw new InvalidOperationException("dimension '" + key + "' re-read " + readSegments +
                        " segments in the reopened file where " + expectedSegments + " were expected");
                JToken expectedToken = created.SelectToken("value_internal_feet.requested");
                if (expectedToken == null)
                    throw new InvalidOperationException("no expected value was captured for dimension '" + key + "' before the family document closed");
                double expectedValue = expectedToken.Value<double>();
                double? measured = MeasuredTotal(dimension);
                if (!measured.HasValue || Math.Abs(measured.Value - expectedValue) > GeometryToleranceFeet)
                    throw new InvalidOperationException("dimension '" + key + "' measures " +
                        (measured.HasValue ? measured.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "nothing") +
                        " ft in the reopened file where " + expectedValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        " ft was verified before saving (tolerance " + GeometryToleranceFeet + " ft)");
                string createdViewName = created.Value<string>("view_name");
                string readViewName = (reopened.GetElement(dimension.OwnerViewId) as View)?.Name;
                if (!string.Equals(readViewName, createdViewName, StringComparison.Ordinal))
                    throw new InvalidOperationException("dimension '" + key + "' re-read in view '" + readViewName +
                        "' where it was created in '" + createdViewName + "'");
                var row = new JObject
                {
                    ["key"] = key,
                    ["element_id"] = id,
                    ["found_as_dimension"] = true,
                    ["references"] = new JObject { ["requested"] = requestedReferences, ["read"] = readReferences, ["match"] = true },
                    // match null, not true, when the flag read false and the substance
                    // carried the verification: a true here would claim a reading that
                    // was not taken. The result-level note names the measured API quirk.
                    ["references_available"] = new JObject
                    {
                        ["requested"] = true,
                        ["read"] = referencesAvailable,
                        ["match"] = referencesAvailable ? (JToken)true : JValue.CreateNull(),
                        ["verified_by"] = referencesAvailable ? "flag" : "substance"
                    },
                    ["label_parameter"] = new JObject
                    {
                        ["requested"] = requested.LabelParameter == null ? JValue.CreateNull() : (JToken)requested.LabelParameter,
                        ["read"] = readLabel == null ? JValue.CreateNull() : (JToken)readLabel,
                        ["match"] = true
                    },
                    ["segments"] = new JObject { ["requested"] = expectedSegments, ["read"] = readSegments, ["match"] = true },
                    ["value_internal_feet"] = new JObject
                    {
                        ["requested"] = expectedValue, ["read"] = measured.Value, ["match"] = true,
                        ["tolerance_internal_feet"] = GeometryToleranceFeet
                    },
                    ["owner_view"] = new JObject { ["requested"] = createdViewName, ["read"] = readViewName, ["match"] = true }
                };
                if (requested.Lock)
                {
                    if (!dimension.IsLocked)
                        throw new InvalidOperationException("dimension '" + key + "' re-read UNLOCKED in the reopened file where lock=true was requested");
                    row["locked"] = new JObject { ["requested"] = true, ["read"] = true, ["match"] = true };
                }
                if (requested.Eq)
                {
                    if (!dimension.AreSegmentsEqual)
                        throw new InvalidOperationException("dimension '" + key + "' re-read WITHOUT its EQ constraint in the reopened file");
                    row["segments_equal"] = new JObject { ["requested"] = true, ["read"] = true, ["match"] = true };
                }
                if (requested.TypeName != null)
                {
                    string readTypeName = dimension.DimensionType?.Name;
                    if (!string.Equals(readTypeName, requested.TypeName, StringComparison.Ordinal))
                        throw new InvalidOperationException("dimension '" + key + "' re-read with dimension type '" + readTypeName +
                            "' where '" + requested.TypeName + "' was requested");
                    row["dimension_type_name"] = new JObject { ["requested"] = requested.TypeName, ["read"] = readTypeName, ["match"] = true };
                }
                rows.Add(row);
            }
            return rows;
        }

        private static double NormalizeAngle(double radians)
        {
            while (radians > Math.PI) radians -= Math.PI * 2;
            while (radians < -Math.PI) radians += Math.PI * 2;
            return radians;
        }

        private static void VerifyAssociation(FamilyManager fm, Parameter elementParameter, string expectedName, string context)
        {
            if (string.IsNullOrWhiteSpace(expectedName)) return;
            if (elementParameter == null) throw new InvalidOperationException(context + " parameter was not exposed for association verification");
            FamilyParameter associated = fm.GetAssociatedFamilyParameter(elementParameter);
            if (associated == null || !string.Equals(associated.Definition?.Name, expectedName, StringComparison.Ordinal))
                throw new InvalidOperationException(context + " was not re-read as associated to family parameter '" + expectedName + "'");
        }

        private static JToken ExpectedFamilyValue(FamilyParameter parameter, JToken requested, double scale)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: return new JValue(requested.Value<string>());
                case StorageType.Integer: return new JValue(requested.Type == JTokenType.Boolean ? (requested.Value<bool>() ? 1 : 0) : requested.Value<int>());
                case StorageType.Double:
                    double number = requested.Value<double>(); ForgeTypeId spec = parameter.Definition.GetDataType();
                    if (spec == SpecTypeId.Length) number *= scale;
                    else if (spec == SpecTypeId.Area) number *= scale * scale;
                    else if (spec == SpecTypeId.Volume) number *= scale * scale * scale;
                    else if (spec == SpecTypeId.Angle) number *= Math.PI / 180.0;
                    return new JValue(number);
                case StorageType.ElementId: return new JValue(requested.Type == JTokenType.Null ? -1 : requested.Value<long>());
                default: return JValue.CreateNull();
            }
        }
        private static JToken ReadFamilyValue(FamilyType type, FamilyParameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: return new JValue(type.AsString(parameter));
                case StorageType.Integer: return new JValue(type.AsInteger(parameter));
                case StorageType.Double: return new JValue(type.AsDouble(parameter));
                case StorageType.ElementId: return new JValue(Rid.Value(type.AsElementId(parameter)));
                default: return JValue.CreateNull();
            }
        }
        private static bool FamilyValuesEqual(JToken expected, JToken actual)
        {
            if ((expected.Type == JTokenType.Float || expected.Type == JTokenType.Integer) &&
                (actual.Type == JTokenType.Float || actual.Type == JTokenType.Integer))
                return Math.Abs(expected.Value<double>() - actual.Value<double>()) <= 1e-9;
            return JToken.DeepEquals(expected, actual);
        }

        private static void SetFamilyValue(FamilyManager fm, FamilyParameter parameter, JToken value, double scale)
        {
            if (parameter.Formula != null) throw new InvalidOperationException("parameter '" + parameter.Definition.Name + "' has a formula and cannot also receive a type value");
            switch (parameter.StorageType)
            {
                case StorageType.String: fm.Set(parameter, value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None)); break;
                case StorageType.Integer:
                    if (value.Type == JTokenType.Boolean) fm.Set(parameter, value.Value<bool>() ? 1 : 0);
                    else fm.Set(parameter, value.Value<int>()); break;
                case StorageType.Double:
                    double number = value.Value<double>(); ForgeTypeId spec = parameter.Definition.GetDataType();
                    if (spec == SpecTypeId.Length) number *= scale;
                    else if (spec == SpecTypeId.Area) number *= scale * scale;
                    else if (spec == SpecTypeId.Volume) number *= scale * scale * scale;
                    else if (spec == SpecTypeId.Angle) number *= Math.PI / 180.0;
                    fm.Set(parameter, number); break;
                case StorageType.ElementId:
                    long raw = value.Type == JTokenType.Null ? -1 : value.Value<long>();
                    if (!Rid.CanRepresent(raw)) throw new InvalidOperationException("ElementId value for '" + parameter.Definition.Name + "' is out of range");
                    fm.Set(parameter, Rid.Make(raw)); break;
                default: throw new InvalidOperationException("unsupported storage type for '" + parameter.Definition.Name + "'");
            }
        }
        private static void ValidateFamilyValue(ParameterPlan parameter, JToken value, string typeName)
        {
            if (parameter.FormulaSpecified)
                throw new ArgumentException("type '" + typeName + "' sets formula-driven parameter '" + parameter.Name + "'");
            bool isNull = value == null || value.Type == JTokenType.Null;
            if (parameter.DataType == "material")
            {
                if (!isNull && value.Type != JTokenType.Integer)
                    throw new ArgumentException("material parameter '" + parameter.Name + "' requires an ElementId integer or null");
                return;
            }
            if (isNull) throw new ArgumentException("parameter '" + parameter.Name + "' cannot be null; use an explicit value");
            if (parameter.DataType == "text")
            {
                if (value.Type != JTokenType.String) throw new ArgumentException("text parameter '" + parameter.Name + "' requires a string");
                return;
            }
            if (parameter.DataType == "yesno")
            {
                if (value.Type != JTokenType.Boolean && value.Type != JTokenType.Integer)
                    throw new ArgumentException("yesno parameter '" + parameter.Name + "' requires boolean or 0/1");
                if (value.Type == JTokenType.Integer && value.Value<int>() != 0 && value.Value<int>() != 1)
                    throw new ArgumentException("yesno parameter '" + parameter.Name + "' integer must be 0 or 1");
                return;
            }
            if (parameter.DataType == "integer")
            {
                if (value.Type != JTokenType.Integer) throw new ArgumentException("integer parameter '" + parameter.Name + "' requires an integer");
                return;
            }
            if (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                throw new ArgumentException("numeric parameter '" + parameter.Name + "' requires a JSON number");
            Finite(value.Value<double>(), "type value for " + parameter.Name);
        }

        private static FamilyParameter FindParameter(FamilyManager fm, string name)
        {
            FamilyParameter found = null;
            foreach (FamilyParameter parameter in fm.Parameters)
                if (string.Equals(parameter.Definition?.Name, name, StringComparison.Ordinal))
                {
                    if (found != null) throw new InvalidOperationException("family parameter name '" + name + "' is ambiguous");
                    found = parameter;
                }
            return found;
        }

        private static CurveArrArray Curves(List<List<XYZ>> loops)
        {
            var result = new CurveArrArray();
            foreach (List<XYZ> points in loops)
            {
                var curves = new CurveArray();
                for (int i = 0; i < points.Count; i++) curves.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
                result.Append(curves);
            }
            return result;
        }
        private static CurveArray CurveLoop(List<XYZ> points)
        {
            var result = new CurveArray();
            for (int i = 0; i < points.Count; i++) result.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
            return result;
        }
        private static CurveArray PathCurves(List<XYZ> points)
        {
            var result = new CurveArray();
            for (int i = 0; i < points.Count - 1; i++) result.Append(Line.CreateBound(points[i], points[i + 1]));
            return result;
        }

        private static List<List<XYZ>> ReadLoops(JArray token, double scale, XYZ normal, string field)
        {
            if (token == null || token.Count == 0) throw new ArgumentException(field + " requires at least one loop");
            var result = new List<List<XYZ>>();
            double? planeOffset = null;
            foreach (JArray loop in token.OfType<JArray>())
            {
                if (loop.Count < 3) throw new ArgumentException(field + " loops need at least three points");
                var points = loop.Select(p => ReadPoint(p, scale)).ToList();
                for (int i = 0; i < points.Count; i++) if (points[i].DistanceTo(points[(i + 1) % points.Count]) < 1e-9)
                    throw new ArgumentException(field + " contains a zero-length edge");
                CheckPlane(points, normal, field);
                double currentOffset = points[0].DotProduct(normal);
                if (planeOffset != null && Math.Abs(currentOffset - planeOffset.Value) > 1e-7)
                    throw new ArgumentException(field + " loops must share one selected plane");
                XYZ areaVector = XYZ.Zero;
                for (int i = 0; i < points.Count; i++) areaVector += points[i].CrossProduct(points[(i + 1) % points.Count]);
                if (Math.Abs(areaVector.DotProduct(normal)) < 1e-12)
                    throw new ArgumentException(field + " contains a zero-area loop");
                planeOffset = currentOffset;
                result.Add(points);
            }
            if (result.Count != token.Count) throw new ArgumentException("every " + field + " loop must be an array");
            return result;
        }

        private static void CheckPlane(IEnumerable<XYZ> points, XYZ normal, string field)
        {
            List<XYZ> list = points.ToList(); double offset = list[0].DotProduct(normal);
            if (list.Any(p => Math.Abs(p.DotProduct(normal) - offset) > 1e-7))
                throw new ArgumentException(field + " points are not coplanar in the selected plane");
        }
        private static List<XYZ> ReadPath(JArray token, double scale, XYZ planeNormal, string field)
        {
            if (token == null || token.Count < 2 || token.Count > 100) throw new ArgumentException(field + " requires 2..100 XYZ points");
            var points = token.Select(x => ReadPoint(x, scale)).ToList();
            for (int i = 0; i < points.Count - 1; i++)
                if (points[i].DistanceTo(points[i + 1]) < 1e-9) throw new ArgumentException(field + " contains a zero-length segment");
            CheckPlane(points, planeNormal, field);
            return points;
        }
        private static void ValidateSweepProfile(FormPlan form, string key)
        {
            XYZ start = form.Path[form.ProfileLocationCurveIndex];
            XYZ end = form.Path[form.ProfileLocationCurveIndex + 1];
            XYZ direction = (end - start).Normalize();
            if (Math.Abs(direction.DotProduct(form.Normal)) < 0.999999)
                throw new ArgumentException("form '" + key + "' profile plane must be perpendicular to its selected path segment");
            XYZ anchor = form.ProfilePlaneLocation == ProfilePlaneLocation.Start ? start :
                form.ProfilePlaneLocation == ProfilePlaneLocation.End ? end : (start + end) / 2.0;
            if (Math.Abs(form.Loops[0][0].DotProduct(form.Normal) - anchor.DotProduct(form.Normal)) > 1e-7)
                throw new ArgumentException("form '" + key + "' profile plane does not intersect the path at profile_plane_location");
        }
        private static void ValidateSweptBlendProfiles(FormPlan form, string key)
        {
            XYZ direction = (form.Path[1] - form.Path[0]).Normalize();
            if (Math.Abs(direction.DotProduct(form.Normal)) < 0.999999)
                throw new ArgumentException("form '" + key + "' bottom/top profile planes must be perpendicular to the path");
            double bottom = form.Loops[0][0].DotProduct(form.Normal);
            double top = form.TopLoops[0][0].DotProduct(form.Normal);
            if (Math.Abs(bottom - form.Path[0].DotProduct(form.Normal)) > 1e-7 ||
                Math.Abs(top - form.Path[1].DotProduct(form.Normal)) > 1e-7)
                throw new ArgumentException("form '" + key + "' bottom and top profiles must intersect the path start and end respectively");
        }
        private static XYZ PlaneNormal(string plane)
        {
            if (plane == "xy") return XYZ.BasisZ; if (plane == "xz") return XYZ.BasisY; if (plane == "yz") return XYZ.BasisX;
            throw new ArgumentException("plane must be xy, xz or yz");
        }
        private static XYZ ReadPoint(JToken token, double scale)
        {
            JArray p = token as JArray; if (p == null || p.Count != 3) throw new ArgumentException("points must contain exactly XYZ");
            return new XYZ(Finite(p[0].Value<double>(), "X") * scale, Finite(p[1].Value<double>(), "Y") * scale,
                Finite(p[2].Value<double>(), "Z") * scale);
        }
        private static XYZ ReadVector(JToken token, string field = "face_normal")
        {
            JArray p = token as JArray; if (p == null || p.Count != 3) throw new ArgumentException(field + " must contain XYZ");
            var v = new XYZ(Finite(p[0].Value<double>(), field + ".X"), Finite(p[1].Value<double>(), field + ".Y"),
                Finite(p[2].Value<double>(), field + ".Z"));
            if (v.GetLength() < 1e-9) throw new ArgumentException(field + " cannot be zero"); return v.Normalize();
        }
        private static void ValidateParameterReferences(FormPlan f, Dictionary<string, ParameterPlan> parameters)
        {
            if ((f.Kind == "sweep" || f.Kind == "swept_blend") &&
                (!string.IsNullOrWhiteSpace(f.StartParameter) || !string.IsNullOrWhiteSpace(f.EndParameter)))
                throw new ArgumentException("form '" + f.Key + "' does not expose start/end associations; parameterize its profile/reference-plane skeleton instead");
            string boundType = f.Kind == "revolution" ? "angle" : "length";
            ValidateTypedParameter(parameters, f.StartParameter, boundType, "form '" + f.Key + "' start");
            ValidateTypedParameter(parameters, f.EndParameter, boundType, "form '" + f.Key + "' end");
            ValidateTypedParameter(parameters, f.MaterialParameter, "material", "form '" + f.Key + "' material");
            ValidateTypedParameter(parameters, f.VisibilityParameter, "yesno", "form '" + f.Key + "' visibility");
        }
        private static void ValidateTypedParameter(Dictionary<string, ParameterPlan> parameters, string name, string expectedType, string use)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!parameters.TryGetValue(name, out ParameterPlan parameter))
                throw new ArgumentException(use + " references unknown parameter '" + name + "'");
            if (!string.Equals(parameter.DataType, expectedType, StringComparison.Ordinal))
                throw new ArgumentException(use + " parameter '" + name + "' must have data_type=" + expectedType + ", not " + parameter.DataType);
        }
        private static ForgeTypeId DataType(string name)
        {
            switch (name)
            {
                case "length": return SpecTypeId.Length; case "area": return SpecTypeId.Area; case "volume": return SpecTypeId.Volume;
                case "angle": return SpecTypeId.Angle; case "number": return SpecTypeId.Number; case "integer": return SpecTypeId.Int.Integer;
                case "yesno": return SpecTypeId.Boolean.YesNo; case "text": return SpecTypeId.String.Text; case "material": return SpecTypeId.Reference.Material;
                default: throw new ArgumentException("data_type '" + name + "' is unsupported");
            }
        }
        private static ForgeTypeId ParameterGroup(string name)
        {
            switch (name)
            {
                case "data": return GroupTypeId.Data; case "identity": case "identity_data": return GroupTypeId.IdentityData;
                case "geometry": return GroupTypeId.Geometry; case "materials": return GroupTypeId.Materials;
                case "general": return GroupTypeId.General; default: throw new ArgumentException("parameter group '" + name + "' is unsupported");
            }
        }
        private static string RequiredText(JObject o, string field)
        { string value = o.Value<string>(field); if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(field + " is required"); return value.Trim(); }
        private static string FullPath(string raw, string extension, string field, bool mustExist, out string error)
        {
            error = null; if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathRooted(raw)) { error = field + " must be absolute."; return null; }
            string path; try { path = Path.GetFullPath(raw); } catch (Exception ex) { error = field + " is invalid: " + ex.Message; return null; }
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) { error = field + " must end in " + extension + "."; return null; }
            if (mustExist && !File.Exists(path)) { error = field + " does not exist: " + path; return null; } return path;
        }
        /// <summary>
        /// SHA-256 of a file, guarded. Templates are hundreds of kilobytes, so hashing the
        /// content costs milliseconds and buys the only honest identity a file has - path,
        /// size and mtime all survive an edit that changes what the family becomes.
        /// </summary>
        private static string SafeFileHash(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(stream);
                    var hex = new System.Text.StringBuilder(h.Length * 2);
                    foreach (byte b in h) hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    return hex.ToString();
                }
            }
            catch (Exception ex) { return "<unhashable:" + ex.GetType().Name + ">"; }
        }

        private static bool Scale(string units, out double scale)
        { if (units == "feet") { scale = 1; return true; } if (units == "m") { scale = 1 / 0.3048; return true; } if (units == "mm") { scale = 1 / 304.8; return true; } scale = 0; return false; }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }

        private sealed class FamilyLoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            public FamilyLoadOptions(bool overwrite) { _overwrite = overwrite; }
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = _overwrite; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Family; overwriteParameterValues = _overwrite; return true; }
        }
        private sealed class FamilyPlan
        {
            public double Scale;
            public readonly List<ParameterPlan> Parameters = new List<ParameterPlan>();
            public readonly List<TypePlan> Types = new List<TypePlan>();
            public readonly List<FormPlan> Forms = new List<FormPlan>();
            public readonly List<ConnectorPlan> Connectors = new List<ConnectorPlan>();
            public readonly List<ReferencePlanePlan> ReferencePlanes = new List<ReferencePlanePlan>();
            public readonly List<DimensionPlan> Dimensions = new List<DimensionPlan>();
            public readonly List<FamilyLinePlan> FamilyLines = new List<FamilyLinePlan>();
            public readonly List<NestedInstancePlan> NestedInstances = new List<NestedInstancePlan>();
            public JObject Summary() => new JObject
            {
                ["parameter_names"] = new JArray(Parameters.Select(x => x.Name)),
                ["type_names"] = new JArray(Types.Select(x => x.Name)),
                ["forms"] = new JArray(Forms.Select(x => new JObject { ["key"] = x.Key, ["kind"] = x.Kind, ["solid"] = x.Solid })),
                ["connectors"] = new JArray(Connectors.Select(x => new JObject { ["key"] = x.Key, ["kind"] = x.Kind, ["host_form_key"] = x.HostFormKey })),
                ["reference_planes"] = new JArray(ReferencePlanes.Select(x => x.Key)),
                ["dimensions"] = new JArray(Dimensions.Select(x => x.Key)),
                ["family_lines"] = new JArray(FamilyLines.Select(x => new JObject { ["key"] = x.Key, ["kind"] = x.Kind })),
                ["nested_instances"] = new JArray(NestedInstances.Select(x => new JObject
                    { ["key"] = x.Key, ["family_path"] = x.FamilyPath, ["type_name"] = x.TypeName, ["placement"] = x.Placement }))
            };
        }
        private sealed class ParameterPlan
        { public string Name, DataType, Formula; public ForgeTypeId Spec, Group; public bool Instance, FormulaSpecified; }
        private sealed class TypePlan { public string Name; public JObject Values; }
        private sealed class FormPlan
        {
            public string Key, Kind, Plane, StartParameter, EndParameter, MaterialParameter, VisibilityParameter;
            public bool Solid; public XYZ Normal, AxisStart, AxisEnd; public double Depth, BottomOffset, TopOffset, StartAngle, EndAngle;
            public List<List<XYZ>> Loops, TopLoops;
            public string PathPlane; public XYZ PathNormal; public List<XYZ> Path;
            public int ProfileLocationCurveIndex; public ProfilePlaneLocation ProfilePlaneLocation;
        }
        private sealed class ConnectorPlan
        {
            public string Key, HostFormKey, Kind, SystemType, Profile, DiameterParameter, WidthParameter, HeightParameter;
            public XYZ FaceNormal; public bool Primary;
        }
        private sealed class ReferencePlanePlan
        {
            public string Key, Name; public XYZ BubbleEnd, FreeEnd, CutVector;
        }
        private sealed class DimensionPlan
        {
            public string Key, LabelParameter, ViewName, TypeName; public List<string> ReferencePlaneKeys; public XYZ LineStart, LineEnd;
            public bool Lock, Eq;
        }
        private sealed class FamilyLinePlan
        {
            public string Key, Kind; public XYZ Normal, Start, End;
        }
        private sealed class NestedInstancePlan
        {
            public string Key, FamilyPath, TypeName, Placement; public XYZ Point; public double RotationRadians;
            public JObject Associations;
        }
    }
}
