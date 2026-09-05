# -*- coding: utf-8 -*-
"""
Mutation harness for horizun_split_multilayer_walls.

    python scripts/wall-split-mutation-harness.py

WHY THIS EXISTS. Two assertions in this capability's test suite shipped VACUOUS -
green whether or not the thing they claimed to check was present:

  * one asserted that "case DependencyKinds.Tag:" appeared in the verifier, and a
    SECOND switch (kinds to failure codes) satisfied it, so deleting the real
    dispatch left it green;
  * one asserted that "already_converted" appeared in the command, and the apply
    reply satisfied it, so deleting it from the DRY RUN left it green.

Neither was findable by reading the test. Both were found by breaking the code and
watching the test not care.

So every critical connection has an entry below: the harness breaks it, runs the
test that is supposed to notice, and requires that test to FAIL. A connection that
survives its own mutation is reported as VACUOUS and is not evidence of anything.

The source files are restored after each mutation, including on failure.
"""
import io, os, subprocess, sys, shutil, time

ROOT = os.getcwd()
SRC = 'src/Horizun.Revit/Commands/'
CORE = 'src/Horizun.Revit/Core/'

MUTATIONS = [
    # (label, file, find, replace, test filter)
    ("P0-1 orden: procedencia despues de elegibilidad",
     SRC + 'WallSplitFacts.cs',
     "            if (ReadProvenanceState(doc, wall, subject, provenance)) return subject;\n\n            // ---- 1. eligibility that needs Revit, before anything is computed ----------\n            subject.Rejection = ReadBlockingConditions(doc, wall, allowArcWalls, subject);\n            if (subject.Rejection != null) return subject;",
     "            subject.Rejection = ReadBlockingConditions(doc, wall, allowArcWalls, subject);\n            if (subject.Rejection != null) return subject;\n            if (ReadProvenanceState(doc, wall, subject, provenance)) return subject;",
     "Provenance_is_read_before_anything_is_planned"),

    ("P0-1 un muro ya convertido vuelve a ser elegible",
     SRC + 'WallSplitFacts.cs',
     "Rejection == null && !AlreadyConverted && Plan != null && Plan.Eligible",
     "Rejection == null && Plan != null && Plan.Eligible",
     "An_already_converted_wall_is_never_eligible"),

    # Un element_ids PRESENTE Y VACIO caia al defecto documentado - la vista, y si
    # no, TODO el documento. Omitirlo significa "sin alcance"; mandarlo vacio es lo
    # que manda un cliente cuyo propio filtro no encontro nada, y leer las dos
    # cosas igual convierte un modelo entero por una seleccion vacia.
    ("P0-0 un element_ids vacio vuelve a ensanchar al documento entero",
     SRC + 'SplitMultilayerWallsCommand.cs',
     '            if (declaredIds != null && declaredIds.Count == 0)',
     '            if (false)',
     "An_empty_element_ids_is_refused_before_the_scope_is_ever_resolved"),

    ("P0-1 el lote pierde el bucket de ya convertidos",
     SRC + 'SplitMultilayerWallsCommand.cs',
     '["already_converted"] = new JArray(alreadyConverted.Select(Converted)),\n                    ["rejected"]',
     '["rejected"]',
     "The_batch_reports_converted_walls_in_their_own_bucket"),

    ("P0-2 el chequeo de hermanos deja de mirar muros extra",
     SRC + 'WallSplitTypes.cs',
     'report["extra_walls_with_this_plan"] = extras;',
     'report["extra_walls_present"] = extras;',
     "The_sibling_check_detects_every_failure_mode_the_review_named"),

    ("P0-2 already_split deja de exigir un solo carrier",
     SRC + 'WallSplitTypes.cs',
     "                carriers.Count == 1 &&\n",
     "",
     "Already_split_is_returned_only_when_nothing_fired"),

    ("P0-2 un campo del sello deja de releerse",
     SRC + 'WallSplitTypes.cs',
     'if (back.ExpectedRoleByLayer != (expectedRoleByLayer ?? "")) return Drift(element, "expected_role_by_layer");',
     '',
     "Every_stamped_field_is_read_back_and_compared"),

    ("P0-3 los elementos en el extremo dejan de compararse",
     SRC + 'WallSplitVerifier.cs',
     "                endElementsOk = end0Now.SequenceEqual(before.ElementsAtEnd0) &&\n                                end1Now.SequenceEqual(before.ElementsAtEnd1);",
     "                endElementsOk = true;",
     "The_elements_at_each_end_are_compared_IN_ORDER"),

    ("P0-3 el orden de corte deja de releerse",
     SRC + 'WallSplitVerifier.cs',
     'report.JoinCheck["cut_order_changed"] = cutOrderChanged;',
     'report.JoinCheck["cut_order_seen"] = cutOrderChanged;',
     "The_cut_order_is_re_read_after_the_restoration"),

    ("P0-4 el token vuelve a llevar solo UniqueIds",
     SRC + 'WallSplitFacts.cs',
     "subject.Dependencies.Where(d => d.Snapshot != null).Select(d => FingerprintOf(d.Snapshot)),",
     "subject.Dependencies.Select(d => d.UniqueId),",
     "The_token_binds_dependency_STATE_and_not_a_list_of_ids"),

    ("P0-4 la expectativa se construye desde el estado releido",
     SRC + 'WallSplitExecutor.cs',
     "                CarrierId = approved.ElementId,\n                CarrierUniqueId = approved.UniqueId,",
     "                CarrierId = now.ElementId,\n                CarrierUniqueId = now.UniqueId,",
     "The_expectation_is_built_from_the_APPROVED_state"),

    ("P0-4 la rotacion sale del fingerprint",
     SRC + 'WallSplitFacts.cs',
     'if (insert.RotationRead) book.AddAngle("insert.rotation", insert.Rotation);',
     '',
     "Every_field_of_a_dependency_snapshot_enters_its_fingerprint"),

    ("P1 el censo inverso deja de mirar cotas",
     SRC + 'WallSplitFacts.cs',
     "                foreach (Dimension dimension in new FilteredElementCollector(doc)\n                             .OfClass(typeof(Dimension)).Cast<Dimension>())\n                    census.IndexDimension(dimension);",
     "",
     "The_reverse_census_asks_the_annotations_rather_than_the_wall"),

    ("P1 el tag vuelve a guardar solo el primer elemento",
     SRC + 'WallSplitVerifier.cs',
     "            bool idsOk = ids.SequenceEqual(before.TaggedElementIds);",
     "            bool idsOk = ids.Count > 0 || before.TaggedElementIds.Count == 0;",
     "A_tag_keeps_its_whole_set_of_tagged_elements"),

    # ---- behavioural mutations on the pure core ----
    ("FactBook: una lista sin orden deja de ordenarse",
     CORE + 'WallLayerRules.cs',
     "            if (!ordered) items.Sort(StringComparer.Ordinal);",
     "",
     "AnUnorderedListIgnoresOrderAndAnOrderedListDoesNot"),

    ("FactBook: las longitudes dejan de cuantizarse",
     CORE + 'WallLayerRules.cs',
     'return Store(name, "q:" + WallLayerRules.QuantizeFeet(feet).ToString(CultureInfo.InvariantCulture));',
     'return Store(name, "q:" + feet.ToString("R", CultureInfo.InvariantCulture));',
     "JitterBelowTheGridDoesNotMoveTheDigestAndARealMoveDoes"),

    ("FactBook: una clave duplicada deja de refusarse",
     CORE + 'WallLayerRules.cs',
     '''            if (_facts.ContainsKey(name))
                throw new ArgumentException(
                    "Fact '" + name + "' was added twice; the second value would silently shadow the first inside " +
                    "the fingerprint.", nameof(name));''',
     '',
     "ADuplicateKeyIsRefusedRatherThanShadowing"),

    # ---- the twelve findings from the adversarial review ----
    ("REV-2/5/11 el re-leido de apply pierde el censo",
     SRC + 'WallSplitExecutor.cs',
     "                                                       options.Reverse, options.Provenance);",
     "                                                       null, null);",
     "The_apply_time_revalidation_reads_the_wall_with_the_SAME_inputs"),

    ("REV-7/10 los openings salen de la prueba de corte",
     SRC + 'WallSplitVerifier.cs',
     "                    case DependencyKinds.Opening:\n                        subjects.Add(new CutSubject\n                        {\n                            Id = dependency.ElementId,\n                            Kind = dependency.Kind,\n                            Bounds = BoundsOf(dependency.OpeningBoundaryPoints)\n                        });\n                        break;\n",
     "",
     "The_cut_proof_covers_openings_and_embedded_walls_not_only_family_instances"),

    ("REV-4 un insert no medible vuelve a descartarse en silencio",
     SRC + 'WallSplitVerifier.cs',
     "            CutSubject unmeasurable = subjects.FirstOrDefault(x => x.Bounds == null);",
     "            CutSubject unmeasurable = null;",
     "An_insert_nobody_could_measure_fails_the_cut_proof"),

    ("REV-7 una prueba de corte vacia vuelve a leerse como verificada",
     SRC + 'WallSplitVerifier.cs',
     'report.CutCoverage["probed"] = false;',
     'report.CutCoverage["skipped"] = false;',
     "An_empty_cut_proof_says_nothing_was_probed_rather_than_reading_as_verified"),

    ("REV-3 el muro embebido deja de comprobar su relacion con el portador",
     SRC + 'WallSplitVerifier.cs',
     'check["still_related_to_carrier"] = relatedToCarrier;',
     'check["embedded_seen"] = relatedToCarrier;',
     "The_embedded_wall_verifier_is_given_the_carrier_and_checks_the_relationship"),

    ("REV-6 el sweep vuelve a medirse solo contra su host",
     SRC + 'WallSplitVerifier.cs',
     '                check["position_deviation_mm"] = Math.Round(deviation, 3);',
     '                check["position_seen_mm"] = Math.Round(deviation, 3);',
     "A_sweep_is_measured_in_model_space_and_not_only_against_its_host"),

    ("REV-6b el sweep olvida el cambio de media anchura de la cara",
     SRC + 'WallSplitVerifier.cs',
     'double expectedSweepOffsetFeet = expected.CarrierOffsetFeet + faceWidthChangeFeet;',
     'double expectedSweepOffsetFeet = expected.CarrierOffsetFeet;',
     "A_sweep_is_measured_in_model_space_and_not_only_against_its_host"),

    ("REV-1 already_split deja de exigir la geometria",
     SRC + 'WallSplitTypes.cs',
     "                geometryOk && geometryMeasured &&\n",
     "",
     "Already_split_requires_the_layers_to_still_BE_where_they_belong"),

    ("REV-9 el rollback deja de mirar Confirmed",
     SRC + 'WallSplitExecutor.cs',
     "                outcome.RollbackConfirmed = rollback.Confirmed;\n                if (!rollback.Confirmed)",
     "                outcome.RollbackConfirmed = true;\n                if (false)",
     "A_rollback_that_did_not_confirm_is_not_reported_as_exactly_as_it_was"),

    ("REV-8 el offset medido vuelve a no asignarse",
     SRC + 'WallSplitExecutor.cs',
     'ObservedOffsetMm = measured?.Value<double?>("observed_offset_mm") ?? double.NaN,',
     '',
     "The_measured_offset_is_actually_measured_and_not_always_zero"),

    ("REV-12 origin_group_param vuelve a callarse",
     SRC + 'WallSplitExecutor.cs',
     'else if (to.IsReadOnly) readOnly.Add(key);',
     'else if (to.IsReadOnly) { }',
     "The_origin_parameter_reports_what_happened_to_it"),

    ("REV-A los parametros vuelven a compararse solo en instancias",
     SRC + 'WallSplitVerifier.cs',
     "                if (failure == null) failure = CompareParameters(after, before, check);",
     "",
     "Parameters_are_compared_for_every_dependency_kind_not_only_instances"),

    ("REV-E la orientacion de la capa deja de comprobarse",
     SRC + 'WallSplitVerifier.cs',
     'check["faces_same_way_as_carrier"] = facingOk;',
     'check["faces_seen"] = facingOk;',
     "A_layer_wall_that_faces_the_wrong_way_fails"),

    ("REV-F la normal deja de contrastarse con Orientation",
     SRC + 'WallSplitFacts.cs',
     "            if (!CorroborateNormal(wall, subject))",
     "            if (false)",
     "The_exterior_normal_is_corroborated_by_a_second_source"),

    ("REV-C un codigo publicado deja de emitirse",
     SRC + 'WallSplitVerifier.cs',
     "WallSplitCodes.VerifyInsertSubcomponents + \"|its nested components changed: \"",
     "\"its nested components changed: \"",
     "Every_published_failure_code_is_emitted_by_some_path"),

    # ---- fase 11: dependencias estructurales ----
    ("F11 el censo estructural deja de preguntar por las barras",
     SRC + 'WallSplitFacts.cs',
     "            Ask(() => host.GetRebarsInHost()?.Select(r => r.Id).ToList());",
     "",
     "The_structural_census_asks_RebarHostData_directly"),

    ("F11 un host no enumerable deja de bloquear",
     SRC + 'WallSplitFacts.cs',
     "            if (!asked)",
     "            if (false)",
     "A_host_whose_reinforcement_cannot_be_enumerated_blocks"),

    ("F11 el containment de armadura deja de decidir",
     SRC + 'WallSplitVerifier.cs',
     "            bool inside = containment.Measured &&\n                          string.Equals(containment.Word, SolidContainment.Inside, StringComparison.Ordinal);",
     "            bool inside = true;",
     "An_unmeasurable_containment_is_not_an_inside_one"),

    ("F11 la zapata deja de comprobar que sigue en el portador",
     SRC + 'WallSplitVerifier.cs',
     'check["wall_is_carrier"] = wallId == Rid.Value(carrier.Id);',
     'check["wall_seen"] = wallId == Rid.Value(carrier.Id);',
     "The_foundation_must_stay_on_the_carrier_and_not_on_a_finish_layer"),

    ("F11 el sistema deja de contar sus miembros",
     SRC + 'WallSplitVerifier.cs',
     'check["members_lost"] = new JArray(lost);',
     'check["members_seen"] = new JArray(lost);',
     "A_reinforcement_system_is_verified_by_its_MEMBERS"),

    ("F11 el cover sale del fingerprint del muro",
     SRC + 'WallSplitFacts.cs',
     "            AddCover(book, wall);",
     "",
     "The_cover_is_part_of_the_walls_own_state_fingerprint"),

    ("F11 las posiciones de barra se ordenan y pierden su secuencia",
     SRC + 'WallSplitFacts.cs',
     '.AddList("rebar.position_digests", snapshot.RebarPositionDigests, ordered: true)',
     '.AddList("rebar.position_digests", snapshot.RebarPositionDigests, ordered: false)',
     "Bar_positions_are_ORDERED_in_the_fingerprint"),

    ("F11 se reimplementa la lectura de armadura en vez de reusarla",
     SRC + 'WallSplitVerifier.cs',
     "            try { described = RebarFacts.Describe(doc, after, includePositions: true); } catch { }",
     "            described = null;",
     "The_rebar_reading_algorithm_is_reused_and_not_reimplemented"),

    ("F11b la captura vuelve a buscar posiciones bajo geometry",
     SRC + 'WallSplitFacts.cs',
     'described?["bar_positions"] is JArray positions',
     'described?["geometry"]?["bar_positions"] is JArray positions',
     "Rebar_position_reading_is_symmetric_and_the_complete_set_is_anchored_in_model_space"),

    ("F11b el verificador deja de usar el mismo lector de posiciones",
     SRC + 'WallSplitVerifier.cs',
     'List<string> nowPositions = WallSplitFacts.ReadRebarPositionDigests(described);',
     'List<string> nowPositions = new List<string>();',
     "Rebar_position_reading_is_symmetric_and_the_complete_set_is_anchored_in_model_space"),

    ("F11b el verificador deja de comparar los offsets de cada posicion",
     SRC + 'WallSplitVerifier.cs',
     'bool positionOffsetsPreserved = nowPositions.SequenceEqual(before.RebarPositionDigests);',
     'bool positionOffsetsPreserved = true;',
     "Rebar_position_reading_is_symmetric_and_the_complete_set_is_anchored_in_model_space"),

    ("F11b el verificador olvida la restriccion a una cara del muro",
     SRC + 'WallSplitVerifier.cs',
     'new { Name = "followed_exterior_face", Offset = expected.CarrierOffsetFeet + faceWidthChangeFeet },',
     'new { Name = "followed_exterior_face", Offset = expected.CarrierOffsetFeet },',
     "Rebar_position_reading_is_symmetric_and_the_complete_set_is_anchored_in_model_space"),

    ("F11b el stirrup vuelve a exigir un solo movimiento rigido",
     SRC + 'WallSplitVerifier.cs',
     'selectedModeCounts[pointMode.Key]++;',
     'selectedModeCounts[modes[0].Name]++;',
     "Rebar_position_reading_is_symmetric_and_the_complete_set_is_anchored_in_model_space"),

    ("F11b un parametro de forma se excusa sin probar geometria",
     SRC + 'WallSplitVerifier.cs',
     'else if (IsVerifiedRebarShapeParameter(after, parameter, check))',
     'else if (after is Rebar)',
     "Only_shape_owned_rebar_dimensions_are_excused_after_geometry_is_proved"),

    ("F12 el cross section vuelve al numero magico",
     SRC + 'WallSplitFacts.cs',
     "            if (section.Value != WallCrossSection.Vertical)",
     "            if (false)",
     "The_cross_section_check_reads_the_TYPED_property_and_no_magic_number"),

    ("F12b la curva original vuelve a ser la referencia viva",
     SRC + 'WallSplitFacts.cs',
     "            subject.LocationCurve = detached;",
     "            subject.LocationCurve = location.Curve;",
     "The_original_curve_is_an_independent_copy_not_the_live_reference"),

    ("F12b las curvas se calculan despues de convertir el portador",
     SRC + 'WallSplitExecutor.cs',
     "            var targetCurves = new Dictionary<int, Curve>();",
     "            var curvesComputedLater = new Dictionary<int, Curve>();",
     "Every_layer_curve_is_built_before_anything_is_written"),

    ("REV-D vuelve el fallback a la referencia viva",
     SRC + 'WallSplitFacts.cs',
     "            if (detached == null)",
     "            if (detached == null) { subject.LocationCurve = location.Curve; }\n            if (false)",
     "There_is_no_fallback_to_the_live_curve_reference"),

    ("REV-D la curva no detachable deja de rechazarse antes de escribir",
     SRC + 'WallSplitFacts.cs',
     "            Curve detached = null;",
     "            Curve detachedCurveHandle = null;",
     "A_curve_that_cannot_be_detached_is_refused_before_any_write"),

    ('CHN-9 el portador vuelve a quedar fuera del mapa de capas',
     SRC + 'WallSplitExecutor.cs',
     '            wallsByLayer[approved.Plan.CoreCarrierLayerIndex] = carrier;',
     '            // the carrier is not in the map',
     'The_executor_builds_a_chain_and_refuses_a_join_across_a_gap'),

    ('CHN-10 la cadena se busca solo en los muros creados',
     SRC + 'WallSplitExecutor.cs',
     '                if (!wallsByLayer.TryGetValue(edge[0], out Wall wa) || !wallsByLayer.TryGetValue(edge[1], out Wall wb))',
     '                if (!created.TryGetValue(edge[0], out Wall wa) || !created.TryGetValue(edge[1], out Wall wb))',
     'The_executor_builds_a_chain_and_refuses_a_join_across_a_gap'),

    ('CHN-11 el grafo se relee sin el portador',
     SRC + 'WallSplitExecutor.cs',
     '            var siblingIds = new HashSet<long>(wallsByLayer.Values.Select(w => Rid.Value(w.Id)));',
     '            var siblingIds = new HashSet<long>(created.Values.Select(w => Rid.Value(w.Id)));',
     'The_executor_builds_a_chain_and_refuses_a_join_across_a_gap'),

    # --- una politica de parametros y una cadena de joins ------------------------
    # Dos tablas del mismo hecho revertian todo muro con puerta, y la estrella
    # creaba joins sobre huecos de 94.5 y 19.5 mm que Revit registra para siempre.

    ('POL-1 el area calculada vuelve a exigir explicacion',
     SRC + '../Core/WallLayerRules.cs',
     '["bip:HOST_AREA_COMPUTED"] = ParameterKind.ComputedByRevit,',
     '["bip:HOST_AREA_COMPUTED"] = ParameterKind.Authored,',
     'A_parameter_Revit_computes_is_never_copied_and_may_change'),

    ('POL-2 la identidad pasa a excusarse',
     SRC + '../Core/WallLayerRules.cs',
     '            return kind == ParameterKind.ComputedByRevit || kind == ParameterKind.ContextDerived;',
     '            return kind != ParameterKind.Authored;',
     'Identity_is_never_copied_and_never_excused'),

    ('POL-3 se copia todo lo que no sea identidad',
     SRC + '../Core/WallLayerRules.cs',
     '        public static bool ShouldCopy(string stableKey) => KindOf(stableKey) == ParameterKind.Authored;',
     '        public static bool ShouldCopy(string stableKey) => KindOf(stableKey) != ParameterKind.Identity;',
     'What_the_operation_sets_itself_is_not_also_copied_generically'),

    ('POL-4 vuelven los nombres de parametro inexistentes',
     SRC + '../Core/WallLayerRules.cs',
     '["bip:ELEM_ROOM_ID"] = ParameterKind.ContextDerived,',
     '["bip:FROM_ROOM_MODULE"] = ParameterKind.ContextDerived,',
     'The_room_parameters_are_the_ones_that_exist'),

    ('POL-5 un parametro desconocido deja de reportarse',
     SRC + '../Core/WallLayerRules.cs',
     '            return ParameterKind.Authored;',
     '            return ParameterKind.ComputedByRevit;',
     'An_unlisted_parameter_is_authored_so_it_is_copied_and_its_change_reported'),

    ('POL-6 el copiador deja de consultar la politica',
     SRC + 'WallSplitExecutor.cs',
     '                    if (!WallLayerRules.ShouldCopy(key)) { skipped.Add(key); continue; }',
     '                    if (false) { skipped.Add(key); continue; }',
     'The_copier_and_the_verifier_read_ONE_policy'),

    ('POL-7 el verificador deja de consultar la politica',
     SRC + 'WallSplitVerifier.cs',
     '                    if (WallLayerRules.MayChangeWithoutExplanation(key))',
     '                    if (false)',
     'The_copier_and_the_verifier_read_ONE_policy'),

    ('CHN-1 el contacto deja de comprobarse',
     SRC + '../Core/WallLayerRules.cs',
     '            return Math.Abs(centres - touching) <= ToleranceFeet;',
     '            return true;',
     'Two_layers_touch_when_the_gap_between_them_is_nothing'),

    ('CHN-2 la cadena vuelve a ser una estrella al portador',
     SRC + '../Core/WallLayerRules.cs',
     '                edges.Add(new[] { orderedMaterialisedLayerIndices[i], orderedMaterialisedLayerIndices[i + 1] });',
     '                edges.Add(new[] { orderedMaterialisedLayerIndices[orderedMaterialisedLayerIndices.Count - 1], orderedMaterialisedLayerIndices[i] });',
     'The_chain_links_consecutive_layers_and_nothing_else'),

    ('CHN-3 una capa sola inventa una arista',
     SRC + '../Core/WallLayerRules.cs',
     '            for (int i = 0; i + 1 < orderedMaterialisedLayerIndices.Count; i++)',
     '            for (int i = 0; i < orderedMaterialisedLayerIndices.Count; i++)',
     'A_single_layer_needs_no_edges_and_none_are_invented'),

    ('CHN-4 la clave de arista depende del orden',
     SRC + '../Core/WallLayerRules.cs',
     '            a <= b ? a + "-" + b : b + "-" + a;',
     '            a + "-" + b;',
     'An_edge_key_does_not_depend_on_which_end_you_start_from'),

    ('CHN-5 el ejecutor deja de exigir contacto antes de unir',
     SRC + 'WallSplitExecutor.cs',
     '                if (!WallLayerRules.LayersTouch(a.ExpectedOffsetFeet, a.WidthFeet,\n                                                b.ExpectedOffsetFeet, b.WidthFeet))',
     '                if (false)',
     'The_executor_builds_a_chain_and_refuses_a_join_across_a_gap'),

    ('CHN-6 el ejecutor deja de releer el grafo',
     SRC + 'WallSplitExecutor.cs',
     '                    if (!expectedEdges.Contains(key))',
     '                    if (false)',
     'The_executor_builds_a_chain_and_refuses_a_join_across_a_gap'),

    ('CHN-7 el verificador deja de exigir la cadena',
     SRC + 'WallSplitVerifier.cs',
     '            if (missing.Count > 0)',
     '            if (false)',
     'The_verifier_holds_the_model_to_the_chain'),

    ('CHN-8 las aristas de mas dejan de rechazarse',
     SRC + 'WallSplitVerifier.cs',
     '            if (extraEdges.Count > 0 || foreignEdges.Count > 0)',
     '            if (false)',
     'The_verifier_holds_the_model_to_the_chain'),

    ('CUT-9 un muro revertido vuelve a publicar sus claims',
     SRC + 'WallSplitExecutor.cs',
     '                foreach (LayerOutcome layer in outcome.Layers) layer.ClaimsWithdrawn = true;',
     '                foreach (LayerOutcome layer in outcome.Layers) { }',
     'A_rolled_back_wall_withdraws_every_verification_claim'),

    ('CUT-11 geometry_verified sobrevive a la retirada',
     SRC + 'WallSplitExecutor.cs',
     '["geometry_verified"] = ClaimsWithdrawn ? (JToken)JValue.CreateNull() : GeometryVerified,',
     '["geometry_verified"] = GeometryVerified,',
     'A_rolled_back_wall_withdraws_every_verification_claim'),

    ('CUT-7 el ejecutor vuelve al All() sin guardia',
     SRC + 'WallSplitExecutor.cs',
     '                    CutVerified = WallLayerRules.CutClaim(layer.IsCoreCarrier, layer.Materialised,\n                                                          coverageProbed, layerChecks, layerClear),',
     '                    CutVerified = layer.IsCoreCarrier || layerRows.All(c => c.Value<bool>("cut_verified")),',
     'The_executor_asks_the_rule_instead_of_calling_All_on_the_cut_checks'),

    ('CUT-8 la cobertura deja de leerse y se asume sondeada',
     SRC + 'WallSplitExecutor.cs',
     '                bool coverageProbed = verdict.CutCoverage != null &&\n                                      (verdict.CutCoverage.Value<bool?>("probed") ?? false);',
     '                bool coverageProbed = true;',
     'The_executor_asks_the_rule_instead_of_calling_All_on_the_cut_checks'),

    # --- el pase vacuo del corte, medido en vivo ---------------------------------
    # cut_coverage.probed=false, cut_checks=0, y cut_verified=TRUE en las 7 capas,
    # membranas de ancho cero incluidas. .All() sobre un conjunto vacio es true.

    ('CUT-1 vuelve el pase vacuo cuando no se sondeo nada',
     SRC + '../Core/WallLayerRules.cs',
     '            if (!coverageProbed) return null;    // the probe never ran',
     '            if (!coverageProbed) return true;    // the probe never ran',
     'A_layer_nobody_probed_makes_no_claim'),

    ('CUT-2 una capa sin fila de sondeo pasa igual',
     SRC + '../Core/WallLayerRules.cs',
     '            if (checksForLayer <= 0) return null; // it ran, but not on this layer',
     '            if (checksForLayer <= 0) return true; // it ran, but not on this layer',
     'A_probe_that_ran_but_not_on_this_layer_makes_no_claim'),

    ('CUT-3 una membrana sin muro reclama corte',
     SRC + '../Core/WallLayerRules.cs',
     '            if (!materialised) return null;      // no wall exists to probe',
     '            if (!materialised) return true;      // no wall exists to probe',
     'A_layer_with_no_volume_makes_no_claim'),

    ('CUT-4 el portador vuelve a declararse verificado',
     SRC + '../Core/WallLayerRules.cs',
     '            if (isCoreCarrier) return null;      // hosts the inserts; nothing to reproduce',
     '            if (isCoreCarrier) return true;      // hosts the inserts; nothing to reproduce',
     'The_carrier_makes_no_claim_because_the_test_does_not_apply_to_it'),

    ('CUT-5 un rayo con material deja de fallar la capa',
     SRC + '../Core/WallLayerRules.cs',
     '            return checksClear >= checksForLayer;',
     '            return true;',
     'One_ray_that_still_found_material_fails_the_layer'),

    ('CUT-6 la razon deja de darse al declinar',
     SRC + '../Core/WallLayerRules.cs',
     '                return "no cut probe ran on this wall, so nothing is claimed about its holes";',
     '                return null;',
     'The_reason_is_stated_whenever_no_claim_is_made'),

    # --- la supresion que el director rechazo, en cada forma en que volveria ----
    # Medido en Revit 2026: convertir un muro de 7 capas con portador en la 05
    # deja DOS avisos permanentes de 'joined but do not intersect'. Meter esos
    # ids en la lista blanca pone all_verified en true y no corrige la geometria.

    ('WARN-1 vuelve la supresion de joined-but-disjoint',
     SRC + 'SplitMultilayerWallsCommand.cs',
     '            BuiltInFailures.OverlapFailures.WallsOverlap\n        };',
     '            BuiltInFailures.OverlapFailures.WallsOverlap,\n            BuiltInFailures.JoinElementsFailures.JoiningDisjoint,\n            BuiltInFailures.JoinElementsFailures.JoiningDisjointWarn\n        };',
     'The_joined_but_disjoint_warning_is_never_suppressed'),

    ('WARN-2 el conjunto de esperadas crece en silencio',
     SRC + 'SplitMultilayerWallsCommand.cs',
     '            BuiltInFailures.OverlapFailures.WallsOverlap\n        };',
     '            BuiltInFailures.OverlapFailures.WallsOverlap,\n            BuiltInFailures.ElementFailures.CannotMoveOrCopy\n        };',
     'Exactly_one_warning_is_expected_by_construction'),

    ('WARN-3 se borra todo aviso en vez de reportarlo',
     SRC + 'SplitMultilayerWallsCommand.cs',
     '                try { _unexpected.Add(failure.GetDescriptionText()); }',
     '                accessor.DeleteWarning(failure);\n                try { _unexpected.Add(failure.GetDescriptionText()); }',
     'A_warning_that_is_not_expected_is_reported_rather_than_deleted'),

    ('WARN-4 el aviso inesperado deja de registrarse',
     SRC + 'SplitMultilayerWallsCommand.cs',
     '                try { _unexpected.Add(failure.GetDescriptionText()); }',
     '                try { }',
     'A_warning_that_is_not_expected_is_reported_rather_than_deleted'),

    # --- RESIDUAL P0: los cuatro hechos que aun se leian de la referencia viva ---
    # Cada una repone el codigo tal como estaba en 0da22b5. Si una prueba no falla
    # al reponerlo, esa prueba no estaba sosteniendo nada.

    ('RES-1 CurveClass vuelve a leerse de la referencia viva',
     SRC + 'WallSplitFacts.cs',
     'subject.CurveClass = detached.GetType().Name;',
     'subject.CurveClass = location.Curve.GetType().Name;',
     'No_curve_fact_is_read_from_the_live_reference_once_the_copy_exists'),

    ('RES-2 la longitud vuelve a medirse sobre la referencia viva',
     SRC + 'WallSplitFacts.cs',
     'if (detached.Length < WallLayerRules.ToleranceFeet)',
     'if (location.Curve.Length < WallLayerRules.ToleranceFeet)',
     'No_curve_fact_is_read_from_the_live_reference_once_the_copy_exists'),

    ('RES-3 la deteccion de Line vuelve a la referencia viva',
     SRC + 'WallSplitFacts.cs',
     'if (detached is Line) return null;',
     'if (location.Curve is Line) return null;',
     'No_curve_fact_is_read_from_the_live_reference_once_the_copy_exists'),

    ('RES-4 la deteccion de Arc vuelve a la referencia viva',
     SRC + 'WallSplitFacts.cs',
     'if (detached is Arc)',
     'if (location.Curve is Arc)',
     'No_curve_fact_is_read_from_the_live_reference_once_the_copy_exists'),

    ('RES-5 el catch recupera con la referencia viva',
     SRC + 'WallSplitFacts.cs',
     'catch (Exception ex) { detachFailure = ex.Message; }',
     'catch (Exception ex) { detachFailure = ex.Message; detached = location.Curve; }',
     'A_throwing_detach_leaves_the_copy_null_so_the_wall_is_refused'),

    ('RES-6 una curva objetivo se recalcula tras ChangeTypeId',
     SRC + 'WallSplitExecutor.cs',
     'Curve layerCurve = targetCurves[layer.LayerIndex];',
     'Curve layerCurve = OffsetCurve(originalCurve, layer.ExpectedOffsetFeet, normal, approved.ArcSign);',
     'Every_layer_curve_is_built_before_anything_is_written'),
]


def run(test_filter):
    result = subprocess.run(
        ['dotnet', 'test', 'tests/Horizun.Core.Tests/Horizun.Core.Tests.csproj',
         '-v', 'q', '--nologo', '--filter', 'FullyQualifiedName~' + test_filter],
        capture_output=True, text=True, cwd=ROOT)
    out = result.stdout + result.stderr
    if 'Failed!' in out or 'error' in out.lower() and 'Passed!' not in out:
        return 'FAIL'
    if 'Passed!' in out:
        return 'PASS'
    return 'UNKNOWN'


def write_text(path, text):
    """Write, with retries, and VERIFY by reading back.

    A mutation harness edits the real source tree. On 2026-08-30 one restore
    raised OSError 22 - a transient lock, an indexer or a build holding the file
    - the harness died on the traceback, and the tree was left carrying the
    mutation. Everything downstream was then reading mutated source: the next
    step in that session was an install. A harness that can poison the tree it is
    testing is more dangerous than the bug it is looking for.

    Returns None on success, or the last error message.
    """
    last = None
    for attempt in range(6):
        try:
            io.open(path, 'w', encoding='utf-8', newline='\n').write(text)
            if io.open(path, encoding='utf-8').read() == text:
                return None
            last = 'read-back did not match what was written'
        except Exception as ex:
            last = '%s: %s' % (type(ex).__name__, ex)
        time.sleep(0.25 * (attempt + 1))
    return last


def restore_or_die(path, original, label):
    """Put the file back, or stop the whole run loudly.

    Never continue with a mutated tree. If the file cannot be restored, the
    original is written beside it so nothing is lost, and the harness exits
    non-zero naming the file - a git checkout of one path is then the fix.
    """
    err = write_text(path, original)
    if err is None:
        return
    rescue = path + '.ORIGINAL'
    try:
        io.open(rescue, 'w', encoding='utf-8', newline='\n').write(original)
    except Exception:
        rescue = '(could not be written either)'
    sys.stdout.write('\n')
    sys.stdout.write('=' * 78 + '\n')
    sys.stdout.write('THE TREE IS STILL MUTATED. Stopping.\n')
    sys.stdout.write('  mutation : %s\n' % label)
    sys.stdout.write('  file     : %s\n' % path)
    sys.stdout.write('  error    : %s\n' % err)
    sys.stdout.write('  original : %s\n' % rescue)
    sys.stdout.write('Restore it before building or installing anything:\n')
    sys.stdout.write('  git checkout -- %s\n' % path)
    sys.stdout.write('=' * 78 + '\n')
    sys.stdout.flush()
    sys.exit(9)


results = []
for label, path, find, replace, test_filter in MUTATIONS:
    full = os.path.join(ROOT, path)
    original = io.open(full, encoding='utf-8').read()

    if find not in original:
        results.append((label, 'ANCHOR-MISSING', test_filter))
        continue

    # sanity: green before
    before = run(test_filter)

    err = write_text(full, original.replace(find, replace, 1))
    if err is not None:
        # The mutation never landed, so there is nothing to undo - but the file
        # may be half-written, so it is restored anyway before moving on.
        restore_or_die(full, original, label)
        results.append((label, 'NOT-APPLIED(' + err + ')', test_filter))
        print('%-58s %s' % (label[:58], 'NOT-APPLIED'))
        sys.stdout.flush()
        continue

    try:
        after = run(test_filter)
    finally:
        restore_or_die(full, original, label)

    verdict = 'BITES' if (before == 'PASS' and after == 'FAIL') else \
              'VACUOUS' if after == 'PASS' else 'INCONCLUSIVE(' + before + '->' + after + ')'
    results.append((label, verdict, test_filter))
    print('%-58s %s' % (label[:58], verdict))
    sys.stdout.flush()

print()
bites = sum(1 for _, v, _ in results if v == 'BITES')
print('MUERDEN: %d / %d' % (bites, len(results)))
for label, verdict, test in results:
    if verdict != 'BITES':
        print('  NO MUERDE: %s -> %s (%s)' % (label, verdict, test))
