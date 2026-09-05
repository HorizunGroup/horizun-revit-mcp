# -*- coding: utf-8 -*-
"""
THE MUTATION LEDGER for the Model Doctor.

A passing test proves nothing on its own: it may be asserting that a name
appears, or that a call was made without checking where its result went. The
only evidence that a test is load-bearing is that BREAKING the thing it
describes makes it fail.

So each entry below reverses one decision in the source, runs the test that
claims to guard it, and records whether that test noticed. A mutation that does
not bite names a test that is not testing.

SAFETY. This edits the real source tree and puts it back. If a restore ever
fails the run STOPS - it does not carry on with a mutated tree, because the next
thing anybody does is build or install it.

    python scripts/model-doctor-mutation-harness.py
"""
import io
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORE = 'src/Horizun.Revit/Core/'
RULES = 'SectionBudgetRules.cs'
REQ = 'ScanRequestRules.cs'
CMD = '../Commands/ModelScanCommand.cs'
ACMD = '../Commands/AuditModelCommand.cs'
WGT = 'WeightAttributionRules.cs'
PGX = 'ScanPagingContext.cs'
WFS = 'WeightAttributionFromScan.cs'

# (label, file relative to CORE, find, replace, test filter)
MUTATIONS = [
    # ---- budgets -----------------------------------------------------------
    ("BUD-1 una seccion desconocida deja de rechazarse", RULES,
     "                if (!known.Contains(prop.Name))",
     "                if (false)",
     "A_section_that_does_not_exist_is_refused_and_the_real_ones_are_named"),

    ("BUD-2 el presupuesto por seccion deja de aplicarse", RULES,
     "                if (s.Limit.HasValue) return s.Limit.Value;",
     "                if (false) return s.Limit.Value;",
     "One_section_can_be_raised_without_raising_any_other"),

    ("BUD-3 el presupuesto por bucket pierde ante el de seccion", RULES,
     "                if (bucket != null && s.BucketLimits.TryGetValue(bucket, out int b)) return b;",
     "                if (false && bucket != null && s.BucketLimits.TryGetValue(bucket, out int b)) return b;",
     "A_bucket_can_be_budgeted_inside_a_section"),

    ("BUD-4 un limite de cero pasa a aceptarse", RULES,
     "            if (n < 1)",
     "            if (n < 0)",
     "A_limit_below_one_is_refused"),

    ("BUD-5 el techo defensivo desaparece", RULES,
     "            if (n > MaxLimit)",
     "            if (false)",
     "A_limit_above_the_ceiling_is_refused_and_the_ceiling_is_named"),

    ("BUD-6 una clave desconocida dentro de la seccion se ignora", RULES,
     "                        if (!SectionKeys.Contains(inner.Name))",
     "                        if (false)",
     "An_unknown_key_inside_a_section_budget_is_refused"),

    ("BUD-7 se honra la mitad buena de una peticion con error", RULES,
     "                    return BudgetPlan.Refused(BudgetCodes.UnknownSection,",
     "                    if (true) { continue; }",
     "One_bad_key_refuses_the_whole_request_rather_than_half_honouring_it"),

    ("BUD-8 section_limits acepta cualquier forma", RULES,
     "            if (sectionLimits.Type != JTokenType.Object)",
     "            if (false)",
     "Section_limits_that_are_not_an_object_are_refused"),

    # ---- paging ------------------------------------------------------------
    ("PAG-1 el total pasa a ser el de la pagina", RULES,
     "            var page = new BucketPage { Total = all.Count };",
     "            var page = new BucketPage();",
     "Every_page_reports_the_whole_population_not_what_is_left"),

    ("PAG-2 el cursor deja de saltar lo ya devuelto", RULES,
     "                : all.Where(r => string.CompareOrdinal(r.Key, afterKey) > 0);",
     "                : all;",
     "Paging_twice_returns_exactly_what_one_call_returns"),

    ("PAG-3 el orden pasa a depender de la cultura", RULES,
     "                .OrderBy(r => r.Key, StringComparer.Ordinal)",
     "                .OrderBy(r => r.Key, StringComparer.InvariantCultureIgnoreCase)",
     "Order_is_ordinal_so_two_machines_page_a_model_the_same_way"),

    ("PAG-4 una pagina completa ofrece cursor igual", RULES,
     "            if (page.Truncated && page.LastKey != null)",
     "            if (page.LastKey != null)",
     "A_complete_page_offers_no_cursor"),

    ("PAG-5 una poblacion vacia se declara truncada", RULES,
     "            page.Truncated = consumed < all.Count;",
     "            page.Truncated = true;",
     "An_empty_population_is_not_a_truncated_one"),

    # ---- cursors -----------------------------------------------------------
    ("CUR-1 un cursor de otro documento se acepta", RULES,
     '            if (!string.Equals(parts[1], documentFingerprint ?? "", StringComparison.Ordinal))',
     "            if (false)",
     "A_cursor_from_another_document_is_refused_not_restarted"),

    ("CUR-2 un cursor de otra seccion se acepta", RULES,
     '            if (!string.Equals(parts[2], section ?? "", StringComparison.Ordinal))',
     "            if (false)",
     "A_cursor_from_another_section_or_bucket_is_refused"),

    ("CUR-3 un cursor de otra version se acepta", RULES,
     "            if (!string.Equals(parts[0], Version, StringComparison.Ordinal))",
     "            if (false)",
     "A_cursor_from_another_contract_version_is_refused"),

    # The anchor matches two CursorMalformed refusals; the harness replaces the
    # FIRST, which is the base64 decode failure - the corrupt-cursor branch this
    # mutation is about.
    ("CUR-4 un cursor corrupto se lee como el principio", RULES,
     "                return CursorRead.Refused(BudgetCodes.CursorMalformed,\n                    \"this cursor could not be decoded.",
     "                return CursorRead.Start(); string unused = (\n                    \"this cursor could not be decoded.",
     "A_corrupt_cursor_is_refused_and_never_read_as_the_start"),

    ("CUR-5 un cursor de forma incorrecta se acepta", RULES,
     "            if (parts.Length != 5)",
     "            if (false)",
     "A_cursor_with_the_right_version_but_the_wrong_shape_is_refused"),

    # ---- the request shape -------------------------------------------------
    ("REQ-1 una opcion desconocida se acepta otra vez", REQ,
     "            if (unknown.Count == 0) return ScanRequestVerdict.Fine();",
     "            if (true) return ScanRequestVerdict.Fine();",
     "A_misspelt_option_is_refused_and_the_real_ones_are_named"),

    ("REQ-2 solo se nombra la primera opcion desconocida", REQ,
     "            List<string> unknown = request.Properties()",
     "            List<string> unknown = request.Properties().Take(1)",
     "Every_unknown_option_is_named_not_just_the_first"),

    ("REQ-3 top vuelve a limitarse por abajo en silencio", REQ,
     "            if (n < 1)",
     "            if (false)",
     "A_top_below_one_is_refused_rather_than_clamped"),

    ("REQ-4 desaparece el techo de top", REQ,
     "            if (n > SectionBudgets.MaxLimit)",
     "            if (false)",
     "A_top_above_the_ceiling_is_refused_and_offered_an_alternative"),

    ("REQ-5 top de otro tipo deja de comprobarse", REQ,
     "            if (top.Type != JTokenType.Integer)",
     "            if (false)",
     "A_top_of_the_wrong_type_is_refused_by_name"),

    ("REQ-6 sections deja de exigir ser un array", REQ,
     "                if (sections.Type != JTokenType.Array)",
     "                if (false)",
     "Sections_given_as_a_bare_string_is_refused_not_read_as_all_of_them"),

    ("REQ-7 una lista vacia vuelve a significar las doce", REQ,
     "                if (((JArray)sections).Count == 0)",
     "                if (false)",
     "An_empty_sections_array_is_refused_because_it_used_to_mean_all_twelve"),

    ("REQ-8 target_parameter ignorado deja de detectarse", REQ,
     "            return requestedSections != null &&",
     "            return false &&",
     "A_target_parameter_with_no_types_section_is_reported_as_doing_nothing"),

    ("REQ-9 el comando deja de comprobar la forma", CMD,
     '            if (!shape.Ok) return CommandResult.Fail(Name + ": " + shape.Message);',
     '            if (false) return CommandResult.Fail(Name + ": " + shape.Message);',
     "The_scan_command_checks_the_request_shape_before_reading_anything"),

    ("REQ-10 el comando vuelve a limitar top con Math.Max", CMD,
     "            if (topToken != null && topToken.Type != JTokenType.Null) top = topToken.Value<int>();",
     '            if (topToken != null) top = Math.Max(1, request.Value<int>("top"));',
     "The_scan_command_checks_the_request_shape_before_reading_anything"),

('WGT-1 aparece una opinion por defecto sobre el peso',
     WGT,
     '            if (profile == null || profile.Type == JTokenType.Null)',
     '            if (false)',
     'Without_a_profile_the_candidates_are_reported_but_NOT_ranked'),

    ('WGT-2 un perfil sin version se acepta',
     WGT,
     '            if (string.IsNullOrWhiteSpace(version))',
     '            if (false)',
     'A_profile_without_a_version_is_refused'),

    ('WGT-3 un peso para un tipo inexistente se ignora',
     WGT,
     '                if (known.Count > 0 && !known.Contains(w.Name))',
     '                if (false)',
     'A_weight_for_a_kind_that_does_not_exist_is_refused'),

    ('WGT-4 un peso negativo se acepta',
     WGT,
     '                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)',
     '                if (double.IsNaN(v) || double.IsInfinity(v))',
     'A_weight_that_is_negative_or_not_a_number_is_refused'),

    ('WGT-5 lo no evaluable vuelve a puntuar cero',
     WGT,
     '            foreach (Contributor c in all.Where(c => c.Status == ContributorStatus.NotAssessable ||',
     '            foreach (Contributor c in all.Where(c => false && c.Status == ContributorStatus.NotAssessable ||',
     'A_contributor_nobody_could_count_is_never_reported_as_zero'),

    ('WGT-6 lo no evaluable entra en el ranking',
     WGT,
     '            List<Contributor> countable = all.Where(c => c.Status == ContributorStatus.Counted ||',
     '            List<Contributor> countable = all.Where(c => true || c.Status == ContributorStatus.Counted ||',
     'A_contributor_nobody_could_count_is_never_reported_as_zero'),

    ('WGT-7 el orden deja de ser total',
     WGT,
     '                .ThenBy(c => c.Kind, StringComparer.Ordinal)',
     '',
     'The_order_is_total_so_two_runs_agree'),

    ('WGT-8 una cota inferior deja de declararse',
     WGT,
     '                          ? " The count is a LOWER BOUND: "',
     '                          ? " ("',
     'A_partly_unreadable_population_ranks_as_a_lower_bound_and_says_so'),

    ('WGT-9 desaparece el aviso de que no son bytes',
     WGT,
     '                ["bytes_are_not_known"] =',
     '                ["note"] =',
     'The_reply_says_out_loud_that_it_is_not_measuring_bytes'),

('EMI-1 categories deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(rows, "categories", "by_category")',
     'paging.Bucket(rows, "lines", "by_category")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-2 cleanliness deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(hardImports, "cleanliness", "cad_imported")',
     'paging.Bucket(hardImports, "types", "cad_imported")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-3 naming deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(views, "naming", "views")',
     'paging.Bucket(views, "links", "views")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-4 documentation deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(noTemplate, "documentation", "views_no_template")',
     'paging.Bucket(noTemplate, "health", "views_no_template")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-5 health deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(rows, "health", "warnings_by_type")',
     'paging.Bucket(rows, "naming", "warnings_by_type")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-6 spatial deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(unplaced, "health", prefix + ".unplaced")',
     'paging.Bucket(unplaced, "worksets", prefix + ".unplaced")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-7 links deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(rows, "links", "rvt_links")',
     'paging.Bucket(rows, "categories", "rvt_links")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-8 worksets deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(rows, "worksets", "worksets")',
     'paging.Bucket(rows, "design_options", "worksets")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-9 design_options deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(rows, "design_options", "design_options")',
     'paging.Bucket(rows, "worksets", "design_options")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-10 types deja de usar su propio presupuesto',
     CMD,
     'paging.Bucket(rows, "types", "types")',
     'paging.Bucket(rows, "cleanliness", "types")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('EMI-11 un bucket se archiva con otro nombre y toma el de la seccion',
     CMD,
     'paging.Bucket(rows, "categories", "by_category")',
     'paging.Bucket(rows, "categories", "global_bucket")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('PGX-1 el limite del bucket deja de aplicarse',
     PGX,
     '            int limit = LimitFor(section, bucket);',
     '            int limit = Plan == null ? 50 : Plan.DefaultLimit;',
     'Each_bucket_gets_its_own_budget_and_the_others_are_untouched'),

    ('PGX-2 el total pasa a ser el de la pagina',
     PGX,
     '            BucketPage page = Paging.Page(rows, limit, afterKey, DocumentFingerprint, section, bucket);',
     '            BucketPage page = Paging.Page(rows.Take(limit).ToList(), limit, afterKey, DocumentFingerprint, section, bucket);',
     'Two_hundred_and_fifty_rows_come_back_whole_in_three_pages'),

    ('PGX-3 un cursor de otro documento deja de reportarse',
     PGX,
     '                else if (!read.Ok &&',
     '                else if (false &&',
     'A_cursor_from_another_document_is_reported_not_silently_restarted'),

    # Aimed at the hash BODY, not the line that concatenates it: that line
    # carries a unit-separator escape, and the anchor had a REAL U+001E where
    # the C# has the six literal characters, so it never matched. Making every
    # hash identical collides the keys exactly as dropping them would.
    ('PGX-4 la clave pierde el hash y colisiona',
     PGX,
     '                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")))',
     '                return "same"; var unused = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")))',
     'Rows_with_no_id_and_a_shared_name_still_have_a_total_order'),

    ('PGX-5 la clave depende del orden de propiedades',
     PGX,
     '                foreach (JProperty p in o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))',
     '                foreach (JProperty p in o.Properties())',
     'The_key_does_not_depend_on_property_order'),

    ('PGX-6 los ids numericos ordenan como texto',
     PGX,
     '                    if (id.Type == JTokenType.Integer) primary = id.Value<long>().ToString("D18", CultureInfo.InvariantCulture);',
     '                    if (id.Type == JTokenType.Integer) primary = id.Value<long>().ToString();',
     'Numeric_ids_order_numerically_not_as_text'),

    ('PGX-7 una cota inferior vuelve a declararse exacta',
     PGX,
     '            o["total_is_exact"] = unreadable <= 0;',
     '            o["total_is_exact"] = true;',
     'An_unreadable_population_makes_its_total_a_lower_bound'),

    ('PGX-8 deja de reportarse que bucket se pagino',
     PGX,
     '            Paged[section + "." + bucket] = new JObject',
     '            if (false) Paged[section + "." + bucket] = new JObject',
     'Every_paged_bucket_is_reported_so_an_ignored_budget_is_visible'),

    ('AUD-1 el audit deja de comprobar la forma',
     ACMD,
     '                if (!shape.Ok) return CommandResult.Fail(Name + ": " + shape.Message);',
     '                if (false) return CommandResult.Fail(Name + ": " + shape.Message);',
     'The_audit_command_checks_the_shape_and_stops_clamping'),

    ('AUD-2 el audit vuelve a recortar top',
     ACMD,
     '                if (topToken != null && topToken.Type != JTokenType.Null) top = topToken.Value<int>();',
     '                if (topToken != null) top = Math.Max(1, request.Value<int>("top"));',
     'The_audit_command_checks_the_shape_and_stops_clamping'),

    ('AUD-3 el audit acepta opciones del scan',
     REQ,
     '            "readiness_roles",\n',
     '            "readiness_roles",\n            "sections",\n            "section_limits",\n',
     'A_scan_only_option_is_not_silently_accepted_by_the_audit'),

    ('AUD-4 las dos herramientas dejan de compartir la regla',
     REQ,
     '            ScanRequestVerdict v = CheckUnknownKeys(request, AuditKnownKeys, "the audit");\n            if (!v.Ok) return v;\n            return CheckTop(request);',
     '            ScanRequestVerdict v = CheckUnknownKeys(request, AuditKnownKeys, "the audit");\n            if (!v.Ok) return v;\n            return ScanRequestVerdict.Fine();',
     'An_audit_top_below_one_is_refused_rather_than_clamped'),

    ('WRT-1 lo no evaluable pasa a contarse como cero',
     WFS,
     '                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && status != null)',
     '                if (false)',
     'A_section_that_threw_makes_everything_it_would_have_counted_not_assessable'),

    ('WRT-2 una seccion no solicitada se cuenta igual',
     WFS,
     '                if (requested != null && !asked.Contains(s.Section))',
     '                if (false)',
     'A_section_nobody_asked_for_is_not_requested_not_zero_and_not_ok'),

    ('WRT-3 un conteo nulo se lee como cero',
     WFS,
     '            if (v.Type == JTokenType.Null)',
     '            if (false)',
     'A_collector_that_failed_leaves_null_and_is_never_read_as_zero'),

    ('WRT-4 el bucket usa returned en vez de total',
     WFS,
     '            JToken total = bo["total"];',
     '            JToken total = bo["returned"];',
     'A_truncated_bucket_still_reports_the_exact_population'),

    ('WRT-5 total_is_exact deja de mirarse',
     WFS,
     '            bool exact = bo["total_is_exact"] != null && bo.Value<bool>("total_is_exact");',
     '            bool exact = true;',
     'A_bucket_whose_total_is_a_lower_bound_says_so_and_stays_ranked'),

    ('WRT-6 la clase epistemologica desaparece del contrato',
     WFS,
     '                    ["evidence_class"] = r.Class,',
     '                    ["evidence_klass"] = r.Class,',
     'Every_candidate_declares_its_class_so_the_contract_cannot_drop_it'),

    ('WRT-7 se omite la version del perfil',
     WFS,
     '                ["profile_version"] = attribution.ProfileVersion,',
     '                ["profile_version"] = null,',
     'The_profile_version_travels_with_the_ranking'),

    ('WRT-8 aparece un ranking por defecto sin perfil',
     WFS,
     '                ["ranked"] = attribution.Ranked,',
     '                ["ranked"] = true,',
     'Without_a_profile_there_is_no_ranking_and_no_version_is_invented'),

    ('WRT-9 un indicador se presenta como medido',
     WFS,
     '                Class = EvidenceClass.Indicator,\n                Why = "an in-place family is unique to its host model and cannot be reused or purged like a " +',
     '                Class = EvidenceClass.Measured,\n                Why = "an in-place family is unique to its host model and cannot be reused or purged like a " +',
     'A_rule_of_thumb_is_carried_as_an_indicator_never_as_a_measurement'),

    ('WRT-10 el hecho renombrado deja de detectarse',
     WFS,
     '            if (v == null)',
     '            if (false)',
     'The_extractor_producing_a_fact_the_rule_does_not_read_is_visible'),

    ('WRT-11 la seccion weight recolecta por su cuenta',
     CMD,
     '                List<Contributor> built = WeightAttributionFromScan.Build(result, sections);',
     '                var extra = new FilteredElementCollector(doc).OfClass(typeof(Group)).GetElementCount();\n                List<Contributor> built = WeightAttributionFromScan.Build(result, sections);',
     'The_weight_section_collects_nothing_of_its_own'),

    ('WRT-12 el perfil deja de leerse de la peticion',
     CMD,
     '                    request["weight_profile"], WeightAttributionFromScan.Kinds);',
     '                    null, WeightAttributionFromScan.Kinds);',
     'The_scan_runs_the_weight_section_over_its_own_output_and_last'),

    # ---------------- level association: three counts, not two ----------------

    ('LVL-1 una medicion vacia se reporta como cero por ciento',
     'LevelAssociationRules.cs',
     '            if (known <= 0) return null;',
     '            if (known <= 0) return 0.0;',
     'A_census_that_measured_nothing_reports_unknown_not_zero_and_not_a_hundred'),

    ('LVL-2 lo ilegible entra al denominador',
     'LevelAssociationRules.cs',
     '            long known = withLevel + withoutLevel;',
     '            long known = withLevel;',
     'An_unreadable_element_is_not_counted_as_a_miss'),

    ('LVL-3 los conteos siempre se declaran exactos',
     'LevelAssociationRules.cs',
     '        public static bool IsExact(long unreadable) { return unreadable == 0; }',
     '        public static bool IsExact(long unreadable) { return true; }',
     'Counts_are_exact_only_when_nothing_was_unreadable'),

    ('LVL-4 la nota deja de avisar que son cotas inferiores',
     'LevelAssociationRules.cs',
     '            if (f.Unreadable > 0)',
     '            if (false)',
     'An_unreadable_element_is_not_counted_as_a_miss'),

    ('LVL-5 un censo sin elementos cae en la frase ordinaria',
     'LevelAssociationRules.cs',
     '            if (f.Examined == 0)',
     '            if (false)',
     'A_census_that_measured_nothing_reports_unknown_not_zero_and_not_a_hundred'),

    ('LVL-6 el desglose se ordena de menor a mayor',
     'LevelAssociationRules.cs',
     '                int byCount = b.Value.CompareTo(a.Value);',
     '                int byCount = a.Value.CompareTo(b.Value);',
     'Categories_are_ranked_largest_first_so_a_reader_sees_where_the_gap_is'),

    ('LVL-7 los empates dejan de romperse por nombre',
     'LevelAssociationRules.cs',
     '                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);',
     '                return byCount != 0 ? byCount : string.CompareOrdinal(b.Key, a.Key);',
     'Ties_break_by_name_so_two_runs_of_one_model_agree'),

    ('LVL-8 el censo deja de decir que no es un hallazgo',
     'LevelAssociationRules.cs',
     '            "a census, not a finding.',
     '            "a list of defects.',
     'The_census_says_in_its_own_words_that_it_is_not_a_finding'),

    ('LVL-9 no haber medido se confunde con haber medido cero',
     'LevelAssociationRules.cs',
     '            if (f == null) return "no census was taken, so nothing is known about level association.";',
     '            if (f == null) f = new LevelAssociationFacts();',
     'No_census_at_all_is_distinguishable_from_a_census_that_found_nothing'),

    # --------------------------- site location ---------------------------

    ('SITE-1 lo no recolectado se responde como no legible',
     'CoordinateRules.cs',
     '                Satisfied = f == null ? (bool?)null : f.SiteReadable,',
     '                Satisfied = f == null ? false : (f.SiteReadable ?? false),',
     'A_site_nobody_collected_is_not_collected_rather_than_missing'),

    ('SITE-2 un sitio sin coordenadas se declara no recolectado',
     'CoordinateRules.cs',
     '                            : "a site location exists but would not report its coordinates")',
     '                            : "not collected")',
     'A_site_that_exists_but_will_not_give_coordinates_says_so'),

    ('SITE-3 se pierde la advertencia de radianes',
     'CoordinateRules.cs',
     '            "latitude and longitude are reported in DEGREES, converted from the radians the API answers in. " +',
     '            "latitude and longitude. " +',
     'The_site_says_in_its_own_words_that_it_is_degrees_and_ungraded'),

    ('SITE-4 solo se publica la latitud',
     'CoordinateRules.cs',
     '                            ? (Fmt(f.LatitudeDegrees.Value) + ", " + Fmt(f.LongitudeDegrees.Value) + " degrees" +',
     '                            ? (Fmt(f.LatitudeDegrees.Value) + " degrees" +',
     'A_readable_site_reports_its_degrees_and_its_place_name'),

    # ------------- the four places a section has to exist at once -------------

    ('SEC-1 una seccion declarada nunca se construye',
     CMD,
     '            Section(result, failed, skipped, sections, "datums", () => DatumsSection(doc, paging));\n',
     '',
     'Every_declared_section_is_actually_built_by_a_call'),

    ('SEC-2 una seccion construida no se puede pedir',
     CMD,
     '            "coordinates", "datums", "level_association",',
     '            "coordinates", "level_association",',
     'Every_built_section_is_declared_so_it_can_be_asked_for'),

    ('SEC-3 el contrato rechaza la seccion que la herramienta anuncia',
     '../../Horizun.Contracts/Contract.cs',
     '""lines"",""types"",""coordinates"",""datums"",""level_association"",""worksharing"",""families"",""views"",""sheets"",""annotations"",""parameters"",""spatial"",""groups"",""design_options_census"",""phases"",""mep"",""structure"",""federation"",""external_content"",""documentary_context"",""delivery_readiness"",""weight""]',
     '""lines"",""types"",""coordinates"",""level_association"",""worksharing"",""families"",""views"",""sheets"",""annotations"",""parameters"",""spatial"",""groups"",""design_options_census"",""phases"",""mep"",""structure"",""federation"",""external_content"",""documentary_context"",""delivery_readiness"",""weight""]',
     'Every_declared_section_is_in_the_contract_the_caller_is_validated_against'),

    ('SEC-4 el contrato ofrece una seccion que nadie implementa',
     '../../Horizun.Contracts/Contract.cs',
     '""lines"",""types"",""coordinates"",""datums"",""level_association"",""worksharing"",""families"",""views"",""sheets"",""annotations"",""parameters"",""spatial"",""groups"",""design_options_census"",""phases"",""mep"",""structure"",""federation"",""external_content"",""documentary_context"",""delivery_readiness"",""weight""]',
     '""lines"",""types"",""coordinates"",""datums"",""level_association"",""worksharing"",""families"",""views"",""sheets"",""annotations"",""parameters"",""spatial"",""groups"",""design_options_census"",""phases"",""a_section_nothing_implements"",""weight""]',
     'The_contract_offers_no_section_the_scan_cannot_build'),

    # Swaps the ORDER: weight is built before the section it ranks. One line,
    # so the reply still has a weight block and only an ordering test sees it.
    ('SEC-5 weight deja de correr al final',
     CMD,
     '            Section(result, failed, skipped, sections, "coordinates", () => CoordinatesSection(doc, paging));',
     '            Section(result, failed, skipped, sections, "weight", () => new JObject()); Section(result, failed, skipped, sections, "coordinates", () => CoordinatesSection(doc, paging));',
     'Weight_is_built_last_because_it_reads_what_the_others_emitted'),

    # ------------- warnings keyed on something that does not move -------------

    ('WARN-1 se vuelve a agrupar por el texto localizado',
     'WarningRules.cs',
     '                bool stable = !string.IsNullOrWhiteSpace(f.DefinitionGuid);',
     '                bool stable = false;',
     'One_warning_in_two_languages_is_one_group'),

    ('WARN-2 la clave guid distingue mayusculas',
     'WarningRules.cs',
     '                    ? f.DefinitionGuid.Trim().ToLowerInvariant()',
     '                    ? f.DefinitionGuid.Trim()',
     'The_guid_key_is_case_insensitive_so_one_warning_is_not_two'),

    ('WARN-3 una identidad inestable se declara estable',
     'WarningRules.cs',
     '                        IdentityIsStable = stable,',
     '                        IdentityIsStable = true,',
     'An_unreadable_guid_falls_back_to_text_and_admits_it'),

    ('WARN-4 un lote de ids ilegible se declara completo',
     'WarningRules.cs',
     '                    g.IdsComplete = false;',
     '                    g.IdsComplete = true;',
     'An_unreadable_id_list_makes_the_group_incomplete_but_not_the_count'),

    ('WARN-5 el mismo elemento se lista dos veces',
     'WarningRules.cs',
     '                    if (!g.FailingElementIds.Contains(id)) g.FailingElementIds.Add(id);',
     '                    g.FailingElementIds.Add(id);',
     'The_same_element_named_twice_is_listed_once'),

    ('WARN-6 las ocurrencias cuentan elementos y no advertencias',
     'WarningRules.cs',
     '                g.Occurrences++;',
     '                g.Occurrences += Math.Max(1, (f.FailingElementIds ?? new List<long>()).Count);',
     'Occurrences_counts_warnings_and_says_so_because_elements_are_a_different_number'),

    ('WARN-7 los grupos se ordenan de menor a mayor',
     'WarningRules.cs',
     '                        .OrderByDescending(g => g.Occurrences)',
     '                        .OrderBy(g => g.Occurrences)',
     'Groups_are_ordered_by_occurrences_then_stably_by_key'),

    ('WARN-8 un perfil ausente se declara valido',
     'WarningRules.cs',
     '                p.Absent = true;\n                p.Ok = false;',
     '                p.Absent = true;\n                p.Ok = true;',
     'No_profile_means_no_warning_was_triaged_and_it_is_not_a_pass'),

    ('WARN-9 se aceptan perfiles con clave de texto',
     'WarningRules.cs',
     '                if (!Guid.TryParse(prop.Name, out parsed))',
     '                if (false)',
     'A_profile_keyed_on_the_description_is_refused_with_the_reason'),

    ('WARN-10 una clave de regla desconocida se ignora',
     'WarningRules.cs',
     '                    if (b.Name != "severity" && b.Name != "label")',
     '                    if (false)',
     'An_unknown_rule_key_is_refused_rather_than_ignored'),

    ('WARN-11 la falta de version se reporta con otro codigo',
     'WarningRules.cs',
     '                p.Code = WarningCodes.NoVersion;',
     '                p.Code = WarningCodes.BadRule;',
     'A_profile_without_a_version_is_refused'),

    ('WARN-12 se pierde el conteo de descripciones distintas',
     'WarningRules.cs',
     '                byKey[k].DistinctDescriptions = descriptions[k].Count;',
     '                byKey[k].DistinctDescriptions = 1;',
     'One_warning_in_two_languages_is_one_group'),

    ('WARN-13 lo legible y lo ilegible se funden por su texto',
     'WarningRules.cs',
     '                    : ("text:" + (f.Description ?? "(description unreadable)"));',
     '                    : (f.Description ?? "(description unreadable)");',
     'A_description_that_reads_like_a_guid_does_not_collide_with_that_guid'),

    ('WARN-14 un perfil valido deja de aplicarse',
     'WarningRules.cs',
     '                g.CallerSeverity = hit.Key;',
     '                g.CallerSeverity = null;',
     'A_triaged_warning_carries_the_callers_severity_beside_revits_own'),


    # ------- the option a tool accepts and the option its schema accepts -------

    # Gives audit_model's schema a property its own unknown-key check refuses,
    # which is the shape a documented-but-rejected option actually has.
    ('KEY-1 el esquema ofrece una opcion que el comando rechaza',
     '../../Horizun.Contracts/Contract.cs',
     '    ""readiness_roles"": { ""type"": ""array"", ""maxItems"": 64,',
     '    ""bogus_option"": { ""type"": ""string"" },    ""readiness_roles"": { ""type"": ""array"", ""maxItems"": 64,',
     'The_schema_offers_no_option_the_command_would_reject'),

    # The anchor matches both tools' schemas; the harness replaces the FIRST,
    # which is model_scan's - enough to make the property stop existing there.
    ('KEY-2 la opcion aceptada no es una propiedad del esquema',
     '../../Horizun.Contracts/Contract.cs',
     '                 } } },\n    ""warning_profile"": { ""type"": ""object"",',
     '                 } } },\n    ""warning_profile_typo"": { ""type"": ""object"",',
     'The_warning_profile_is_a_top_level_property_of_both_tools'),

    # ---------------- naming: every class accounted for, always ----------------

    ('NAM-1 una clase desaparece de la respuesta',
     'NamingFromScan.cs',
     '            foreach (string cls in NamingClasses.All)',
     '            foreach (string cls in NamingClasses.All.Where(c => c != "grids"))',
     'Every_class_the_profile_can_mention_appears_in_the_answer'),

    ('NAM-2 lo no recolectado se reporta como correcto',
     'NamingFromScan.cs',
     '                        ["status"] = NamingStatus.NotCollected,',
     '                        ["status"] = NamingStatus.Ok,',
     'A_class_nobody_collected_is_reported_as_a_defect_in_this_tool'),

    ('NAM-3 una poblacion nula se trata como vacia',
     'NamingFromScan.cs',
     '                if (populations == null || !populations.TryGetValue(cls, out things) || things == null)',
     '                if (populations == null || !populations.TryGetValue(cls, out things))',
     'A_null_population_is_not_collected_rather_than_empty'),

    ('NAM-4 lo inaplicable se reporta como correcto',
     'NamingFromScan.cs',
     '                        ["status"] = NamingStatus.NotApplicable,',
     '                        ["status"] = NamingStatus.Ok,',
     'A_class_that_cannot_exist_here_is_not_applicable_not_an_empty_pass'),

    ('NAM-5 una poblacion vacia gana sobre la ausencia declarada',
     'NamingFromScan.cs',
     '                if (na.TryGetValue(cls, out reason))',
     '                if (false && na.TryGetValue(cls, out reason))',
     'Not_applicable_wins_over_a_population_that_was_also_supplied'),

    ('NAM-6 lo no pedido cuenta como evaluado',
     'NamingFromScan.cs',
     '                if (v.Status == NamingStatus.Ok || v.Status == NamingStatus.Failed) assessed++;',
     '                assessed++;',
     'A_class_the_profile_is_silent_about_is_not_counted_as_assessed'),

    ('NAM-7 un perfil ausente se reporta como rechazado',
     'NamingFromScan.cs',
     '            if (!p.Ok && p.Code == NamingCodes.NoProfile)',
     '            if (false)',
     'With_no_profile_nothing_is_assessed_and_nothing_is_declared_clean'),

    ('NAM-8 la poblacion deja de decir que abarca',
     'NamingFromScan.cs',
     '{ "families", "every loadable Family. System families are not Family elements and are not here." },',
     '{ "families", "every family." },',
     'Every_class_publishes_what_its_population_actually_was'),

    ('NAM-9 el comando deja de recolectar una clase',
     CMD,
     '            collect("grids", () => named(new FilteredElementCollector(doc).OfClass(typeof(Grid))));',
     '',
     'Every_naming_class_is_collected_or_explicitly_declared_absent'),

    ('NAM-10 la respuesta deja de decir que ninguno es un aprobado',
     'NamingFromScan.cs',
     '                            "has no findings, and NONE of them is a pass. Only \'ok\' means a rule ran over a " +',
     '                            "has no findings. Only \'ok\' means a rule ran over a " +',
     'The_reply_says_that_none_of_the_three_empty_states_is_a_pass'),

    ('NAM-11 el esquema ofrece naming_profile y el comando lo rechaza',
     REQ,
     '            "naming_profile",',
     '',
     'The_schema_offers_no_option_the_command_would_reject'),

    # ---------- ownership: four states that add up, taken without taking ----------

    ('OWN-1 lo ilegible se cuenta como libre',
     'OwnershipCensus.cs',
     '            if (state == CheckoutState.Unreadable) { Unreadable++; return; }',
     '            if (state == CheckoutState.Unreadable) { NotOwned++; return; }',
     'Unknown_is_not_unowned'),

    ('OWN-2 el invariante deja de comprobarse',
     'OwnershipCensus.cs',
     '            return t.OwnedByMe + t.OwnedByOthers + t.NotOwned + t.Unreadable == t.Scanned;',
     '            return true;',
     'An_unbalanced_tally_is_reported_as_unbalanced_rather_than_hidden'),

    ('OWN-3 un censo vacio reporta cero por ciento',
     'OwnershipCensus.cs',
     '            if (t == null || t.Scanned <= 0) return null;',
     '            if (t == null) return null; if (t.Scanned <= 0) return 0.0;',
     'A_census_that_scanned_nothing_reports_unknown_not_zero'),

    ('OWN-4 la cuota excluye lo ilegible del denominador',
     'OwnershipCensus.cs',
     '            return Math.Round(t.OwnedByOthers * 100.0 / t.Scanned, 4);',
     '            return Math.Round(t.OwnedByOthers * 100.0 / (t.Scanned - t.Unreadable), 4);',
     'The_share_is_over_everything_scanned_including_the_unreadable'),

    ('OWN-5 un duenno sin nombre se descarta',
     'OwnershipCensus.cs',
     '            string key = string.IsNullOrWhiteSpace(owner) ? "(owner name unreadable)" : owner;',
     '            if (string.IsNullOrWhiteSpace(owner)) return; string key = owner;',
     'An_element_whose_owner_will_not_name_itself_is_still_counted_as_owned'),

    ('OWN-6 la nota deja de avisar que son cotas inferiores',
     'OwnershipCensus.cs',
     '            if (t.Unreadable > 0)',
     '            if (false)',
     'Unknown_is_not_unowned'),

    ('OWN-7 un censo sin elementos cae en la frase ordinaria',
     'OwnershipCensus.cs',
     '            if (t.Scanned == 0)',
     '            if (false)',
     'A_census_that_scanned_nothing_reports_unknown_not_zero'),

    ('OWN-8 los duennos se ordenan de menor a mayor',
     'OwnershipCensus.cs',
     '                int byCount = b.Value.CompareTo(a.Value);',
     '                int byCount = a.Value.CompareTo(b.Value);',
     'Owners_are_ranked_largest_first_and_ties_break_by_name'),

    ('OWN-9 un modelo no compartido reporta ceros en vez de ausencia',
     'OwnershipCensus.cs',
     '                ["status"] = "not_applicable",\n                ["reason"] = reason,',
     '                ["status"] = "not_applicable",\n                ["reason"] = reason,\n                ["elements_scanned"] = 0,\n                ["elements_owned_by_others"] = 0,',
     'A_document_that_is_not_workshared_has_absent_counts_not_zero_ones'),

    ('OWN-10 el censo deja de decir que no toma nada',
     'OwnershipCensus.cs',
     '            "a census taken WITHOUT relinquishing anything',
     '            "a census of ownership',
     'The_census_says_it_took_nothing_and_that_borrowing_is_not_a_defect'),

    ('OWN-11 la seccion suelta la propiedad para medirla',
     CMD,
     '                    CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, e.Id);',
     '                    WorksharingUtils.RelinquishOwnership(doc, null, null); CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, e.Id);',
     'The_worksharing_section_neither_takes_nor_releases_ownership'),

    ('OWN-12 el recorrido empieza antes de saber si esta compartido',
     CMD,
     '            if (workshared != true)',
     '            var unusedWalk = new FilteredElementCollector(doc); if (workshared != true)',
     'A_document_that_is_not_workshared_short_circuits_before_any_element_is_read'),

    # ------ workset placement: may fail on partial coverage, never pass ------

    ('WKP-1 una cobertura incompleta puede aprobar',
     'WorksetPlacementRules.cs',
     '            return coverageComplete ? WorksetGate.Pass : WorksetGate.NotAssessable;',
     '            return WorksetGate.Pass;',
     'An_incomplete_check_may_fail_but_can_never_pass'),

    ('WKP-2 una cobertura incompleta deja de poder fallar',
     'WorksetPlacementRules.cs',
     '            if (found > max.Value) return WorksetGate.Fail;',
     '            if (found > max.Value) return coverageComplete ? WorksetGate.Fail : WorksetGate.NotAssessable;',
     'An_incomplete_check_may_fail_but_can_never_pass'),

    ('WKP-3 sin techo declarado se aprueba igual',
     'WorksetPlacementRules.cs',
     '            if (!max.HasValue) return WorksetGate.NotAssessable;',
     '            if (!max.HasValue) return WorksetGate.Pass;',
     'A_count_with_no_threshold_can_neither_pass_nor_fail'),

    ('WKP-4 un workset cerrado deja de romper la cobertura',
     'WorksetPlacementRules.cs',
     '            return worksetsClosed == 0 && worksetUnreadable == 0;',
     '            return worksetUnreadable == 0;',
     'Coverage_is_complete_only_when_nothing_was_closed_and_nothing_unreadable'),

    ('WKP-5 la nota de cobertura deja de decir que no puede aprobar',
     'WorksetPlacementRules.cs',
     '            return string.Join("; ", parts) + ". Every count here is a LOWER BOUND, and this check cannot PASS.";',
     '            return string.Join("; ", parts) + ".";',
     'The_coverage_note_names_what_was_missed_and_says_it_cannot_pass'),

    ('WKP-6 una categoria sin regla se cuenta como mal ubicada',
     'WorksetPlacementRules.cs',
     '                if (!rules.ExpectedByCategory.TryGetValue(e.Category, out want)) continue;',
     '                if (!rules.ExpectedByCategory.TryGetValue(e.Category, out want)) want = null;',
     'A_category_the_rules_are_silent_about_is_unjudged_not_misplaced'),

    ('WKP-7 un workset ilegible se reporta como mal ubicado',
     'WorksetPlacementRules.cs',
     '                if (e.ActualWorkset == null) continue;',
     '                if (e.ActualWorkset == null) { }',
     'An_element_whose_workset_could_not_be_read_is_not_reported_as_misplaced'),

    ('WKP-8 sin reglas se juzga igual',
     'WorksetPlacementRules.cs',
     '            if (observed == null || rules == null || !rules.Ok) return found;',
     '            if (observed == null || rules == null) return found;',
     'A_refused_rule_set_is_not_applied_even_though_it_parsed_some_rules'),

    # Found because the identically-shaped workset guard went VACUOUS: a refused
    # profile arrives holding the entries it parsed before the bad key.
    ('WARN-15 un perfil rechazado se aplica igual',
     'WarningRules.cs',
     '            if (groups == null || profile == null || !profile.Ok) return;',
     '            if (groups == null || profile == null) return;',
     'A_refused_profile_is_not_applied_even_though_it_parsed_some_entries'),

    ('WKP-9 el nombre por defecto se compila adentro',
     'WorksetPlacementRules.cs',
     '            if (worksetNames == null || rules == null || !rules.Ok || rules.DefaultWorksetNames.Count == 0)\n                return hits;',
     '            if (worksetNames == null || rules == null || !rules.Ok) return hits;\n            if (rules.DefaultWorksetNames.Count == 0) rules.DefaultWorksetNames.Add("Workset1");',
     'With_no_declared_default_names_nothing_is_flagged_as_a_default'),

    ('WKP-10 una clave desconocida se ignora',
     'WorksetPlacementRules.cs',
     '                        r.Code = WorksetRuleCodes.UnknownKey;',
     '                        break;\n                        r.Code = WorksetRuleCodes.UnknownKey;',
     'An_unknown_key_refuses_the_whole_rule_set_with_the_offender_named'),

    ('WKP-11 un techo de cero se lee como ausente',
     'WorksetPlacementRules.cs',
     '                        r.MaxElementsInWrongWorkset = max;',
     '                        r.MaxElementsInWrongWorkset = max == 0 ? (long?)null : max;',
     'A_ceiling_of_zero_is_a_real_ceiling_and_not_an_absent_one'),

    ('WKP-12 una cuota sobre nada se reporta como cero',
     'WorksetPlacementRules.cs',
     '            if (scanned <= 0) return null;',
     '            if (scanned <= 0) return 0.0;',
     'A_share_of_nothing_scanned_is_unknown_rather_than_zero'),

    # ---- the emitter guard, after it was rebuilt to read across lines ----
    #
    # Both of these were INVISIBLE to the line-based walk this replaced: it
    # matched a single line, so a wrapped call simply did not exist for it.

    ('GRD-1 una llamada partida en lineas se archiva en otra seccion',
     CMD,
     '                ["outliers"] = paging.Bucket(outlierRows, "coordinates", "outliers"),',
     '                ["outliers"] = paging.Bucket(\n'
     '                    outlierRows,\n'
     '                    "datums",\n'
     '                    "outliers"),',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    ('GRD-2 una llamada partida se presupuesta con otro nombre',
     CMD,
     '                ["links"] = paging.Bucket(linkRows, "coordinates", "links")',
     '                ["links"] = paging.Bucket(\n'
     '                    linkRows,\n'
     '                    "coordinates",\n'
     '                    "outliers")',
     'Every_emitter_files_its_buckets_under_its_own_section'),

    # ---------------- families: three kinds, and nothing is weighed ----------------

    ('FAM-1 lo ilegible se cuenta como cargable',
     'FamilyCensusRules.cs',
     '            long unreadable = all.Count(f => f.Kind == FamilyKind.Unreadable);',
     '            long unreadable = 0;',
     'An_unreadable_family_is_never_counted_as_a_loadable_one'),

    ('FAM-2 el flag compartido ilegible se lee como falso',
     CMD,
     '                    f.IsShared = shared == null ? (bool?)null : shared.AsInteger() != 0;',
     '                    f.IsShared = shared != null && shared.AsInteger() != 0;',
     'The_family_census_reads_the_shared_flag_as_three_valued'),

    ('FAM-3 los candidatos dejan de declararse indicadores',
     'FamilyCensusRules.cs',
     '                    ["evidence"] = EvidenceClass.Indicator,',
     '                    ["evidence"] = EvidenceClass.Measured,',
     'Candidates_are_indicators_and_say_so_in_every_row'),

    ('FAM-4 el triage deja de decir cuantas familias omitio',
     'FamilyCensusRules.cs',
     '                ["not_selected"] = ranked.Count - selected.Count,',
     '                ["not_selected"] = 0,',
     'A_triage_states_how_many_families_it_passed_over'),

    ('FAM-5 el orden de candidatos deja de ser estable',
     'FamilyCensusRules.cs',
     '                .ThenBy(f => f.Name, StringComparer.Ordinal)',
     '                .ThenByDescending(f => f.Name, StringComparer.Ordinal)',
     'The_ranking_is_stable_so_two_runs_of_one_model_agree'),

    ('FAM-6 un in-place ilegible se juzga como in-place',
     'FamilyCensusRules.cs',
     '                if (f.IsInPlace == true && f.Category != null)',
     '                if (f.IsInPlace != false && f.Category != null)',
     'A_family_whose_in_place_flag_is_unreadable_is_not_reported_as_in_place'),

    ('FAM-7 lo no compartido ilegible se reporta como incumplimiento',
     'FamilyCensusRules.cs',
     '                if (f.Name != null && f.IsShared == false &&',
     '                if (f.Name != null && f.IsShared != true &&',
     'A_family_expected_to_be_shared_is_flagged_only_when_the_model_says_it_is_not'),

    ('FAM-8 un perfil rechazado se aplica igual',
     'FamilyCensusRules.cs',
     '            if (families == null || p == null || !p.Ok) return findings;',
     '            if (families == null || p == null) return findings;',
     'A_refused_profile_is_not_applied_even_though_it_parsed_earlier_rules'),

    ('FAM-9 una excepcion explicita deja de honrarse',
     'FamilyCensusRules.cs',
     '                if (f.Name != null && p.Exceptions.Contains(f.Name)) continue;',
     '                if (false) continue;',
     'An_explicit_exception_is_honoured'),

    ('FAM-10 una lista de categorias vacia prohibe todo en silencio',
     'FamilyCensusRules.cs',
     '                        if (p.AllowedCategories.Count == 0)',
     '                        if (false)',
     'An_empty_allowed_categories_list_is_refused_rather_than_banning_everything'),

    ('FAM-11 un tipo ilegible deja de romper la cobertura',
     'FamilyCensusRules.cs',
     '            get { return UnreadableTypeCount == 0 && UnreadableInstanceCount == 0 && NameReadable; }',
     '            get { return true; }',
     'An_unreadable_type_or_instance_makes_the_family_incomplete'),

    ('FAM-12 los parametros ilegibles se reportan como cero',
     'FamilyCensusRules.cs',
     '                ["parameter_count"] = f.ParametersReadable ? (JToken)f.ParameterCount : null,',
     '                ["parameter_count"] = f.ParameterCount,',
     'A_family_whose_parameters_could_not_be_read_reports_null_not_zero'),

    ('FAM-13 la seccion abre la familia para medirla',
     CMD,
     '                try { f.ParameterCount = fam.Parameters == null ? 0 : fam.Parameters.Size; }',
     '                try { var fd = doc.EditFamily(fam); f.ParameterCount = fam.Parameters.Size; }',
     'The_family_census_never_opens_a_family_document'),

    ('FAM-14 las familias de sistema dejan de recolectarse',
     CMD,
     '                if (et is FamilySymbol) continue;    // that is a loadable family\'s type',
     '                if (true) continue;',
     'A_system_family_is_collected_by_a_route_that_is_not_OfClass_Family'),

    ('FAM-15 una instancia sin simbolo se atribuye igual',
     'FamilyCensusRules.cs',
     '                ["instances_unreadable"] = all.Sum(f => f.UnreadableInstanceCount),',
     '                ["instances_unreadable"] = 0,',
     'Instances_and_types_that_could_not_be_read_are_reported_in_the_totals'),

    # -------- views: five statuses, and not every view has every property --------

    ('VIS-1 una leyenda se juzga como si tuviera nivel',
     'ViewFactsRules.cs',
     '                case ViewProperties.Level:\n                case ViewProperties.ViewRange:\n                    return Plans.Contains(viewType);',
     '                case ViewProperties.Level:\n                case ViewProperties.ViewRange:\n                    return true;',
     'A_legend_has_no_level_and_that_is_not_a_failure'),

    ('VIS-2 un schedule se juzga como si tuviera recorte',
     'ViewFactsRules.cs',
     '                case ViewProperties.CropActive:\n                    return Croppable.Contains(viewType);',
     '                case ViewProperties.CropActive:\n                    return true;',
     'A_schedule_has_no_crop_and_that_is_not_a_failure'),

    ('VIS-3 lo ilegible se reporta como incumplimiento',
     'ViewFactsRules.cs',
     '                if (f.Unreadable.Contains(prop))',
     '                if (false)',
     'A_property_whose_read_threw_is_not_readable_and_not_a_failure'),

    ('VIS-4 lo no aplicable pierde ante lo ilegible',
     'ViewFactsRules.cs',
     '                if (!ViewApplicability.Applies(prop, f.ViewType))',
     '                if (!ViewApplicability.Applies(prop, f.ViewType) && !f.Unreadable.Contains(prop))',
     'Not_applicable_wins_over_not_readable_because_the_property_does_not_exist'),

    ('VIS-5 sin perfil se declara conforme',
     'ViewFactsRules.cs',
     '                if (rule == null)',
     '                if (false)',
     'With_no_profile_every_applicable_property_is_not_requested'),

    ('VIS-6 un tipo de vista desconocido se acepta',
     'ViewFactsRules.cs',
     '                if (known.Count > 0 && !known.Contains(prop.Name))',
     '                if (false)',
     'A_profile_naming_a_view_type_this_revit_does_not_have_is_refused'),

    ('VIS-7 una excepcion explicita deja de honrarse',
     'ViewFactsRules.cs',
     '            if (excepted) rule = null;',
     '            excepted = false;',
     'An_explicit_exception_makes_every_property_not_requested_again'),

    ('VIS-8 un perfil rechazado se aplica igual',
     'ViewFactsRules.cs',
     '            if (p != null && p.Ok && f.ViewType != null && p.ByViewType.TryGetValue(f.ViewType, out rule))',
     '            if (p != null && f.ViewType != null && p.ByViewType.TryGetValue(f.ViewType, out rule))',
     'A_refused_profile_is_not_applied_even_though_it_parsed_earlier_types'),

    ('VIS-9 una escala fuera de lista se aprueba',
     'ViewFactsRules.cs',
     '                    return r.AllowedScales.Contains(f.Scale.Value)',
     '                    return true ? V(prop, ViewPropertyStatus.Ok, "1:" + f.Scale.Value) : true',
     'A_scale_outside_the_allowed_list_fails_and_one_inside_passes'),

    ('VIS-10 un filtro requerido ausente deja de nombrarse',
     'ViewFactsRules.cs',
     '                        (missing.Count > 0 ? "missing: " + string.Join(", ", missing) + ". " : "") +',
     '                        (missing.Count > 0 ? "some filters are missing. " : "") +',
     'A_required_filter_that_is_missing_fails_and_names_it'),

    ('VIS-11 el recuento mezcla los cinco estados',
     'ViewFactsRules.cs',
     '                        case ViewPropertyStatus.NotApplicable: notApplicable++; break;',
     '                        case ViewPropertyStatus.NotApplicable: ok++; break;',
     'The_tally_keeps_the_five_statuses_apart'),

    ('VIS-12 una plantilla se juzga como un dibujo',
     CMD,
     '                verdicts.Add(f.IsTemplate ? new List<ViewPropertyVerdict>()\n                                          : ViewFactsRules.Judge(f, profile));',
     '                verdicts.Add(ViewFactsRules.Judge(f, profile));',
     'A_view_template_is_never_judged_against_rules_written_for_drawings'),

    ('VIS-13 las vistas internas de Revit se juzgan igual',
     CMD,
     '                if (ViewApplicability.IsInternal(f.ViewType)) { internalViews++; continue; }',
     '                if (false) { internalViews++; continue; }',
     'Revits_own_internal_views_are_excluded_before_anything_is_judged'),

    ('VIS-14 un plano se cuenta entre las vistas',
     CMD,
     '                if (v is ViewSheet) continue;',
     '                if (false) continue;',
     'A_sheet_is_not_counted_among_the_views'),

    ('VIS-15 una lectura fallida deja de nombrar su propiedad',
     CMD,
     '            catch { f.Unreadable.Add(property); }',
     '            catch { }',
     'Every_view_property_read_is_guarded_and_names_the_property_it_failed'),

    # ---- sheets: not-empty is not complete, a schedule is not a viewport ----

    ('SHT-1 un plano de schedules se declara vacio',
     'SheetAnnotationRules.cs',
     '        public bool IsEmpty { get { return ViewportCount + ScheduleInstanceCount == 0; } }',
     '        public bool IsEmpty { get { return ViewportCount == 0; } }',
     'A_sheet_of_schedules_has_no_viewports_and_is_not_empty'),

    ('SHT-2 los duplicados dejan de calcularse sin regla',
     'SheetAnnotationRules.cs',
     '                .Where(g => g.Count() > 1)',
     '                .Where(g => false)',
     'A_duplicate_sheet_number_is_a_fact_computed_whether_or_not_a_rule_asks'),

    ('SHT-3 un numero ilegible se cuenta como duplicado',
     'SheetAnnotationRules.cs',
     '                .Where(s => s != null && s.NumberReadable && !string.IsNullOrWhiteSpace(s.Number))',
     '                .Where(s => s != null)',
     'An_unreadable_number_is_not_counted_as_a_duplicate_or_as_empty'),

    ('SHT-4 un rotulo ilegible se reporta como ausente',
     'SheetAnnotationRules.cs',
     '                if (!s.Unreadable.Contains("title_blocks"))',
     '                if (true)',
     'A_title_block_count_that_could_not_be_read_produces_no_finding'),

    ('SHT-5 el minimo de viewports cuenta tambien los schedules',
     'SheetAnnotationRules.cs',
     '                if (r.MinViewports.HasValue && s.ViewportCount < r.MinViewports.Value)',
     '                if (r.MinViewports.HasValue && s.ViewportCount + s.ScheduleInstanceCount < r.MinViewports.Value)',
     'Viewport_bounds_count_viewports_and_never_the_schedules'),

    ('SHT-6 un minimo imposible se acepta',
     'SheetAnnotationRules.cs',
     '            if (r.MinViewports.HasValue && r.MaxViewports.HasValue && r.MinViewports > r.MaxViewports)',
     '            if (false)',
     'A_minimum_above_the_maximum_is_refused_as_unsatisfiable'),

    ('SHT-7 se aplica un minimo de anotaciones que nadie declaro',
     'SheetAnnotationRules.cs',
     '            if (views == null || r == null || !r.Ok || r.MinAnnotationsByViewType.Count == 0) return below;',
     '            if (views == null || r == null || !r.Ok) return below;\n            if (r.MinAnnotationsByViewType.Count == 0) r.MinAnnotationsByViewType["Section"] = 5;',
     'A_view_with_one_dimension_is_not_a_documented_view'),

    ('SHT-8 el minimo se aplica a tipos de vista que no nombra',
     'SheetAnnotationRules.cs',
     '                if (!r.MinAnnotationsByViewType.TryGetValue(v.ViewType, out min)) continue;',
     '                if (!r.MinAnnotationsByViewType.TryGetValue(v.ViewType, out min)) min = 5;',
     'A_declared_minimum_is_applied_only_to_the_view_types_it_names'),

    ('SHT-9 una clase de anotacion desaparece del desglose',
     'SheetAnnotationRules.cs',
     '            foreach (string k in AnnotationKinds.All)',
     '            foreach (string k in AnnotationKinds.All.Where(x => x != AnnotationKinds.Tags))',
     'Annotation_counts_are_kept_per_kind_because_they_are_not_interchangeable'),

    ('SHT-10 lo ilegible se suma al total de anotaciones',
     'SheetAnnotationRules.cs',
     '        public long Total { get { return ByKind.Values.Sum(); } }',
     '        public long Total { get { return ByKind.Values.Sum() + Unreadable; } }',
     'Unreadable_annotations_are_counted_apart_from_the_kinds'),

    ('SHT-11 un juego de reglas rechazado se aplica igual',
     'SheetAnnotationRules.cs',
     '            if (sheets == null || r == null || !r.Ok) return findings;',
     '            if (sheets == null || r == null) return findings;',
     'A_refused_rule_set_is_not_applied_even_though_it_parsed_earlier_rules'),

    ('SHT-12 una excepcion explicita deja de honrarse',
     'SheetAnnotationRules.cs',
     '                if (s.Number != null && r.Exceptions.Contains(s.Number)) continue;',
     '                if (false) continue;',
     'An_explicit_exception_is_honoured'),

    ('SHT-13 una clave desconocida se ignora',
     'SheetAnnotationRules.cs',
     '                        r.Code = SheetRuleCodes.UnknownKey;',
     '                        break;\n                        r.Code = SheetRuleCodes.UnknownKey;',
     'An_unknown_key_refuses_the_whole_rule_set'),

    ('SHT-14 los schedules se cuentan como viewports en el comando',
     CMD,
     '            foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))',
     '            foreach (Element e in new List<Element>())',
     'A_schedule_on_a_sheet_is_collected_apart_from_the_viewports'),

    ('SHT-15 el censo de anotaciones incluye elementos del modelo',
     CMD,
     '                        if (!e.ViewSpecific) { notViewSpecific++; continue; }\n                        long viewId = Rid.Value(e.OwnerViewId);\n                        AnnotationCensus c;\n                        if (!byView.TryGetValue(viewId, out c))\n                        {\n                            var v = doc.GetElement(e.OwnerViewId) as View;\n                            c = new AnnotationCensus\n                            {\n                                ViewId = viewId,\n                                ViewName = v == null ? null : SafeName(v),\n                                ViewType = v == null ? null : SafeViewType(v)\n                            };\n                            byView[viewId] = c;\n                        }\n                        long had;\n                        c.ByKind[kind.Key] = c.ByKind.TryGetValue(kind.Key, out had) ? had + 1 : 1;',
     '                        long viewId = Rid.Value(e.OwnerViewId);\n                        AnnotationCensus c;\n                        if (!byView.TryGetValue(viewId, out c))\n                        {\n                            var v = doc.GetElement(e.OwnerViewId) as View;\n                            c = new AnnotationCensus\n                            {\n                                ViewId = viewId,\n                                ViewName = v == null ? null : SafeName(v),\n                                ViewType = v == null ? null : SafeViewType(v)\n                            };\n                            byView[viewId] = c;\n                        }\n                        long had;\n                        c.ByKind[kind.Key] = c.ByKind.TryGetValue(kind.Key, out had) ? had + 1 : 1;',
     'The_annotation_census_counts_only_view_specific_elements'),

    ('SHT-16 los tags se recolectan por una lista fija de categorias',
     CMD,
     '                         new KeyValuePair<string, Type>(AnnotationKinds.Tags, typeof(IndependentTag)),',
     '                         new KeyValuePair<string, Type>(AnnotationKinds.Tags, typeof(TextNote)),',
     'Tags_are_collected_by_class_because_they_span_many_categories'),

    # ---- parameter standards: a parameter is not its name ----

    ('PAR-1 una regla por GUID se satisface con el nombre',
     'ParameterStandardRules.cs',
     '            if (r.KeyedByGuid && !string.Equals(r.Guid, o.Guid, StringComparison.OrdinalIgnoreCase))',
     '            if (false)',
     'The_right_name_with_the_wrong_guid_does_not_satisfy_a_guid_rule'),

    ('PAR-2 el ambito equivocado se reporta como ausente',
     'ParameterStandardRules.cs',
     '            if ((r.Scope == ParameterScope.Type) != o.IsType)',
     '            if (false)',
     'A_type_rule_read_on_an_instance_is_wrong_scope_and_not_a_missing_parameter'),

    ('PAR-3 lo ilegible se reporta como ausente',
     'ParameterStandardRules.cs',
     '            if (!o.Readable)',
     '            if (false)',
     'An_unreadable_parameter_is_neither_missing_nor_satisfied'),

    ('PAR-4 un placeholder se acepta como valor',
     'ParameterStandardRules.cs',
     '            if (r.Placeholders.Any(ph => string.Equals(ph, value, StringComparison.OrdinalIgnoreCase)))',
     '            if (false)',
     'Missing_empty_and_placeholder_are_three_different_answers'),

    ('PAR-5 un vacio se acepta como presente',
     'ParameterStandardRules.cs',
     '                return Set(v, r.AllowEmpty ? ParameterOutcome.Present : ParameterOutcome.Empty,',
     '                return Set(v, ParameterOutcome.Present,',
     'Missing_empty_and_placeholder_are_three_different_answers'),

    ('PAR-6 una categoria ajena se declara conforme',
     'ParameterStandardRules.cs',
     '            if (!categoryHit || !classHit)',
     '            if (false)',
     'A_category_the_rule_does_not_name_is_not_applicable_rather_than_passing'),

    ('PAR-7 un parametro opcional ausente se reporta como faltante',
     'ParameterStandardRules.cs',
     '                return Set(v, r.Required ? ParameterOutcome.Missing : ParameterOutcome.RuleNotRequested,',
     '                return Set(v, ParameterOutcome.Missing,',
     'An_optional_parameter_that_is_absent_is_not_requested_rather_than_missing'),

    # The regex is SKIPPED instead of refusing the profile - which is the
    # behaviour that makes an unrunnable rule report every value as acceptable.
    ('PAR-8 una regex invalida se omite en vez de rechazarse',
     'ParameterStandardRules.cs',
     '                    catch (Exception ex)\n'
     '                    {\n'
     '                        return Bad(p, ParameterRuleCodes.BadRegex,\n'
     '                            "rule \'" + r.Id + "\' has an invalid regex (" + ex.Message + "). It is REFUSED " +\n'
     '                            "rather than skipped: a rule that silently does not run reports every value as " +\n'
     '                            "acceptable.");\n'
     '                    }',
     '                    catch (Exception)\n'
     '                    {\n'
     '                        r.Pattern = null;\n'
     '                    }',
     'An_invalid_regex_is_refused_rather_than_skipped'),

    ('PAR-9 un rango incoherente se acepta',
     'ParameterStandardRules.cs',
     '                if (r.Min.HasValue && r.Max.HasValue && r.Min > r.Max)',
     '                if (false)',
     'An_incoherent_range_and_an_incompatible_unit_are_refused'),

    ('PAR-10 dos reglas con el mismo id se aceptan',
     'ParameterStandardRules.cs',
     '                if (!seenIds.Add(r.Id))',
     '                if (false)',
     'Two_rules_with_one_id_are_refused'),

    ('PAR-11 una regla sin identidad se acepta',
     'ParameterStandardRules.cs',
     '                if (string.IsNullOrWhiteSpace(r.Name) && string.IsNullOrWhiteSpace(r.Guid) &&',
     '                if (false && string.IsNullOrWhiteSpace(r.Guid) &&',
     'A_rule_that_names_no_parameter_is_refused'),

    ('PAR-12 un ambito contradictorio se acepta',
     'ParameterStandardRules.cs',
     '                if (r.ExpectedBinding != null && r.ExpectedBinding != r.Scope)',
     '                if (false)',
     'A_contradictory_scope_and_binding_is_refused'),

    ('PAR-13 un perfil rechazado se evalua igual',
     'ParameterStandardRules.cs',
     '            if (observations == null || p == null || !p.Ok) return verdicts;',
     '            if (observations == null || p == null) return verdicts;',
     'A_refused_profile_is_not_evaluated_even_though_it_read_earlier_rules'),

    ('PAR-14 la regex del llamador corre sin limite de tiempo',
     'ParameterStandardRules.cs',
     '        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);',
     '        public static readonly TimeSpan RegexTimeout = Regex.InfiniteMatchTimeout;',
     'Nothing_from_a_profile_is_executed_and_the_reply_says_so'),

    ('PAR-15 una regla que no aplica a nada se acepta',
     'ParameterStandardRules.cs',
     '                if (r.Categories != null && r.Categories.Count == 0 && r.ElementClasses.Count == 0)',
     '                if (false)',
     'A_rule_that_applies_to_nothing_is_refused'),

    ('PAR-16 el recuento funde los trece resultados',
     'ParameterStandardRules.cs',
     '                if (v != null && v.Outcome != null && counts.ContainsKey(v.Outcome)) counts[v.Outcome]++;',
     '                if (v != null) counts[ParameterOutcome.Present]++;',
     'The_tally_keeps_all_thirteen_outcomes_apart'),

    ('PAR-17 la especificacion se lee del grupo, ausente en 2027',
     CMD,
     '                ForgeTypeId spec = p.Definition == null ? null : p.Definition.GetDataType();',
     '                ForgeTypeId spec = null; var unused = p.Definition.ParameterGroup;',
     'The_specification_is_read_from_GetDataType_and_never_from_ParameterGroup'),

    ('PAR-18 un parametro de tipo pierde sus instancias',
     CMD,
     '                                o.AffectedInstanceIds.AddRange(users);',
     '                                users.Clear();',
     'A_type_parameter_is_observed_once_with_its_instances_attached'),

    # ---- rooms, spaces and areas: one condition cannot express four states ----

    ('SPA-1 lo no colocado se deriva del area',
     'SpatialCensusRules.cs',
     '            if (f.HasLocation == false) return SpatialState.Unplaced;',
     '            if (f.AreaSqM.HasValue && f.AreaSqM.Value <= 0) return SpatialState.Unplaced;',
     'An_unplaced_room_is_unplaced_and_not_merely_zero_area'),

    ('SPA-2 un redundante se reporta como no cerrado',
     'SpatialCensusRules.cs',
     '            if (f.IsRedundant == true) return SpatialState.Redundant;',
     '            if (false) return SpatialState.Redundant;',
     'A_redundant_room_is_redundant_even_though_revit_also_calls_it_unenclosed'),

    ('SPA-3 un area cero sin causa se declara no cerrado',
     'SpatialCensusRules.cs',
     '            if (f.AreaSqM.Value <= 0) return SpatialState.ZeroArea;',
     '            if (f.AreaSqM.Value <= 0) return SpatialState.NotEnclosed;',
     'A_zero_area_that_nothing_else_explains_is_reported_as_zero_area'),

    ('SPA-4 una ubicacion ilegible se lee como no colocado',
     'SpatialCensusRules.cs',
     '            if (f.HasLocation == null) return SpatialState.Unreadable;',
     '            if (f.HasLocation == null) return SpatialState.Unplaced;',
     'An_unreadable_element_is_never_any_of_the_four'),

    ('SPA-5 las tres poblaciones se cuentan juntas',
     'SpatialCensusRules.cs',
     '                .Where(f => f != null && f.Kind == kind).ToList();',
     '                .Where(f => f != null).ToList();',
     'Rooms_spaces_and_areas_are_counted_apart'),

    ('SPA-6 un esquema de area se inventa en una habitacion',
     'SpatialCensusRules.cs',
     '                ["area_scheme"] = f.Kind == SpatialKind.Area ? f.AreaScheme : null,',
     '                ["area_scheme"] = f.AreaScheme,',
     'An_area_scheme_is_reported_on_an_area_and_never_invented_on_a_room'),

    ('SPA-7 los duplicados se cruzan entre poblaciones',
     'SpatialCensusRules.cs',
     '                .Where(f => f != null && f.Kind == kind && f.NumberReadable &&',
     '                .Where(f => f != null && f.NumberReadable &&',
     'Duplicate_numbers_are_found_within_a_kind_and_never_across_kinds'),

    ('SPA-8 lo ilegible deja de romper la exactitud',
     'SpatialCensusRules.cs',
     '        o["counts_are_exact"] = counts[SpatialState.Unreadable] == 0;',
     '        o["counts_are_exact"] = true;',
     'One_unreadable_element_makes_the_counts_inexact'),

    ('SPA-9 un numero ilegible se cuenta como vacio',
     'SpatialCensusRules.cs',
     '        public bool NumberEmpty { get { return NumberReadable && string.IsNullOrWhiteSpace(Number); } }',
     '        public bool NumberEmpty { get { return string.IsNullOrWhiteSpace(Number); } }',
     'An_unreadable_number_is_neither_empty_nor_a_duplicate'),

    ('SPA-10 la redundancia se adivina del area en el comando',
     CMD,
     '                    f.IsRedundant = flagged.TryGetValue(f.ElementId, out guids) &&',
     '                    f.IsRedundant = f.AreaSqM.HasValue && f.AreaSqM.Value <= 0 &&',
     'Redundancy_is_never_inferred_from_a_zero_area'),

    ('SPA-11 el cerramiento se deriva del area en el comando',
     CMD,
     '                    IList<IList<BoundarySegment>> b = se.GetBoundarySegments(opts);\n                    f.IsEnclosed = b != null && b.Count > 0;',
     '                    f.IsEnclosed = se.Area > 0;',
     'Enclosure_comes_from_the_boundary_and_placement_from_the_location'),

    # ---- groups: unplaced is not empty. options: none is not a pass ----

    ('GRP-1 un tipo sin instancias se declara vacio',
     'GroupOptionRules.cs',
     '        public bool? Empty { get { return MemberCount.HasValue ? (bool?)(MemberCount.Value == 0) : null; } }',
     '        public bool? Empty { get { return InstanceCount == 0; } }',
     'A_type_with_no_instances_is_unplaced_and_not_empty'),

    ('GRP-2 los dos conteos se funden en uno',
     'GroupOptionRules.cs',
     '                ["types_with_no_members"] = t.Count(x => x.Empty == true),',
     '                ["types_with_no_members"] = t.Count(x => x.Unplaced),',
     'The_two_counts_are_reported_separately_and_never_merged'),

    ('GRP-3 miembros ilegibles se cuentan como vacio',
     'GroupOptionRules.cs',
     '                ["types_whose_members_are_unreadable"] = t.Count(x => x.Empty == null),',
     '                ["types_whose_members_are_unreadable"] = 0,',
     'A_type_whose_members_could_not_be_read_is_neither_empty_nor_full'),

    ('GRP-4 un anidamiento desconocido se cuenta como plano',
     'GroupOptionRules.cs',
     '                ["nesting_unreadable"] = i.Count(x => x.IsNested == null),',
     '                ["nesting_unreadable"] = 0,',
     'Nested_instances_are_counted_and_unknown_nesting_is_not_counted_as_flat'),

    ('GRP-5 las categorias dominantes pierden su orden',
     'GroupOptionRules.cs',
     '                int byCount = b.Value.CompareTo(a.Value);\n                return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);',
     '                return string.CompareOrdinal(a.Key, b.Key);',
     'Member_categories_are_ranked_largest_first_and_stably'),

    ('GRP-6 un documento sin opciones se declara aprobado',
     'GroupOptionRules.cs',
     '                ["status"] = "not_applicable",\n                ["reason"] = "this document defines no design option sets, so there is nothing to report about " +',
     '                ["status"] = "ok",\n                ["reason"] = "this document defines no design option sets, so there is nothing to report about " +',
     'A_document_with_no_design_options_is_not_applicable_rather_than_clean'),

    ('GRP-7 una primacia ilegible se lee como secundaria',
     'GroupOptionRules.cs',
     '                ["is_primary"] = f.IsPrimary,',
     '                ["is_primary"] = f.IsPrimary ?? false,',
     'An_option_whose_primacy_could_not_be_read_is_null_and_not_secondary'),

    ('GRP-8 un tipo sin colocar declara cero miembros',
     CMD,
     '                        f.MemberCount = null;\n                        f.MembersReadable = false;',
     '                        f.MemberCount = 0;\n                        f.MembersReadable = true;',
     'A_group_type_that_is_never_placed_reports_an_unknown_member_count'),

    ('GRP-9 el documento sin opciones sigue recorriendo elementos',
     CMD,
     '            if (sets.Count == 0) return GroupOptionRules.NoDesignOptions();',
     '            if (false) return GroupOptionRules.NoDesignOptions();',
     'A_document_with_no_design_options_short_circuits_before_the_element_walk'),

    # ---- phases: a category without phases is not a category missing one ----

    ('PHA-1 una categoria sin fases se reporta como sin fase',
     'PhaseCensusRules.cs',
     '            if (!f.SupportsPhases) return PhaseState.NotApplicable;',
     '            if (!f.SupportsPhases) return PhaseState.NoPhase;',
     'A_category_without_phases_is_not_applicable_and_never_no_phase'),

    ('PHA-2 los dos estados se cuentan juntos',
     'PhaseCensusRules.cs',
     '            foreach (PhasedElementFact e in all) counts[StateOf(e)]++;',
     '            foreach (PhasedElementFact e in all) counts[e.SupportsPhases ? StateOf(e) : PhaseState.NoPhase]++;',
     'The_two_are_counted_apart_in_the_tally'),

    ('PHA-3 un orden desconocido se declara invalido',
     'PhaseCensusRules.cs',
     '                if (!CreatedSequence.HasValue || !DemolishedSequence.HasValue) return null;',
     '                if (!CreatedSequence.HasValue || !DemolishedSequence.HasValue) return true;',
     'An_unknown_order_is_not_an_invalid_one'),

    ('PHA-4 una demolicion normal se declara contradiccion',
     'PhaseCensusRules.cs',
     '                return DemolishedSequence.Value < CreatedSequence.Value;',
     '                return DemolishedSequence.Value != CreatedSequence.Value;',
     'A_normal_demolition_is_not_a_contradiction'),

    ('PHA-5 las fases se ordenan por nombre',
     'PhaseCensusRules.cs',
     '                .OrderBy(p => p.Sequence)',
     '                .OrderBy(p => p.Name, StringComparer.Ordinal)',
     'Phases_are_ordered_by_sequence_and_never_alphabetically'),

    ('PHA-6 lo ilegible deja de romper la exactitud',
     'PhaseCensusRules.cs',
     '            o["counts_are_exact"] = counts[PhaseState.Unreadable] == 0;',
     '            o["counts_are_exact"] = true;',
     'An_unreadable_element_is_its_own_state_and_makes_the_counts_inexact'),

    ('PHA-7 se pierde el desglose por categoria',
     'PhaseCensusRules.cs',
     '            foreach (PhasedElementFact e in all.Where(x => StateOf(x) == PhaseState.NoPhase))',
     '            foreach (PhasedElementFact e in new List<PhasedElementFact>())',
     'The_no_phase_breakdown_names_the_categories_so_a_reader_can_act'),

    ('PHA-8 se deja de contar cuantas categorias se examinaron',
     'PhaseCensusRules.cs',
     '            o["categories_examined"] = all.Select(e => e.Category).Where(c => c != null)\n                                          .Distinct(StringComparer.Ordinal).Count();',
     '            o["categories_examined"] = 0;',
     'The_number_of_categories_examined_is_reported_beside_the_counts'),

    ('PHA-9 la aplicabilidad se decide por una lista fija',
     CMD,
     '                    if (created == null && demolished == null)',
     '                    if (f.Category == "Levels" || f.Category == "Grids")',
     'Phase_applicability_is_asked_of_the_element_not_read_from_a_list'),

    ('PHA-10 la secuencia de fases deja de leerse del documento',
     CMD,
     '                    sequenceOf[id] = i;',
     '                    sequenceOf[id] = 0;',
     'Phase_order_comes_from_the_document_sequence_and_never_from_the_name'),

    # ---- MEP: connectivity is not calculation ----

    ('MEP-1 lo ilegible se cuenta como sin sistema',
     'MepCensusRules.cs',
     '                if (!Readable || !SystemCount.HasValue) return MepSystemState.Unreadable;',
     '                if (!Readable) return MepSystemState.Unreadable;\n                if (!SystemCount.HasValue) return MepSystemState.NoSystem;',
     'An_element_whose_system_could_not_be_read_is_not_an_element_without_one'),

    ('MEP-2 varios sistemas se funden en tener uno',
     'MepCensusRules.cs',
     '                if (SystemCount.Value > 1) return MepSystemState.MultipleSystems;',
     '                if (false) return MepSystemState.MultipleSystems;',
     'Multiple_systems_is_reported_rather_than_merged_into_having_one'),

    ('MEP-3 el balance de conectores deja de comprobarse',
     'MepCensusRules.cs',
     '                ["connectors_balance"] = connected + open + unreadable == connectors,',
     '                ["connectors_balance"] = true,',
     'The_connector_counts_are_published_as_balancing_or_not'),

    ('MEP-4 un conector ilegible deja de romper la exactitud',
     'MepCensusRules.cs',
     '                ["counts_are_exact"] = counts[MepSystemState.Unreadable] == 0 && unreadable == 0,',
     '                ["counts_are_exact"] = counts[MepSystemState.Unreadable] == 0,',
     'An_unreadable_connector_makes_the_counts_inexact'),

    ('MEP-5 una clasificacion ilegible se cuenta como ausente',
     'MepCensusRules.cs',
     '                .Where(s => s != null && s.ClassificationReadable && string.IsNullOrWhiteSpace(s.Classification))',
     '                .Where(s => s != null && string.IsNullOrWhiteSpace(s.Classification))',
     'A_system_with_no_classification_is_apart_from_one_that_could_not_be_read'),

    ('MEP-6 se deja de decir que conectividad no es calculo',
     'MepCensusRules.cs',
     '            "connectivity is NOT calculation. Nothing here says a system is balanced, sized, calculated or " +',
     '            "the systems as modelled. " +',
     'The_section_says_connectivity_is_not_calculation'),

    ('MEP-7 un conector abierto se declara defecto',
     'MepCensusRules.cs',
     '            "an open connector is a FACT, not a defect. A duct ending at a shaft, a pipe waiting for equipment " +',
     '            "an open connector is an issue to fix. A duct ending at a shaft, a pipe waiting for equipment " +',
     'An_open_connector_is_a_fact_and_never_a_defect'),

    ('MEP-8 un elemento MEP se busca por categoria fija',
     CMD,
     '                if (cm == null) continue;      // not an MEP element; not a finding',
     '                if (cm == null) { elements.Add(new MepElementFact { ElementId = Rid.Value(e.Id) }); continue; }',
     'An_mep_element_is_found_by_its_connectors_and_not_by_a_category_list'),

    # ---- structure: modelling facts only ----

    ('STR-1 un host ilegible se cuenta como sin host',
     'StructureCensusRules.cs',
     '                .Where(r => r != null && r.HasHost == false)',
     '                .Where(r => r != null && r.HasHost != true)',
     'Rebar_whose_host_could_not_be_read_is_never_counted_as_hostless'),

    ('STR-2 un recubrimiento ilegible se reporta como cero',
     'StructureCensusRules.cs',
     '                ["cover_mm"] = r.CoverReadable ? r.CoverMm : null,',
     '                ["cover_mm"] = r.CoverMm ?? 0,',
     'A_cover_that_could_not_be_read_is_null_and_not_zero'),

    ('STR-3 una poblacion no recolectada desaparece',
     'StructureCensusRules.cs',
     '                byPopulation[name] = f == null\n                    ? new JObject { ["population"] = name, ["status"] = "not_collected" }\n                    : ToJson(f);',
     '                if (f != null) byPopulation[name] = ToJson(f);',
     'Every_population_appears_even_when_it_was_not_collected'),

    ('STR-4 lo ilegible deja de romper la exactitud',
     'StructureCensusRules.cs',
     '                ["counts_are_exact"] = pops.All(p => p.CountsAreExact) && bars.All(r => r.Readable),',
     '                ["counts_are_exact"] = true,',
     'An_unreadable_element_makes_its_population_inexact_and_the_summary_too'),

    ('STR-5 se deja de decir que no evalua seguridad',
     'StructureCensusRules.cs',
     '            "these are MODELLING facts. Nothing here assesses safety, capacity, adequacy or code compliance, " +',
     '            "these are structural findings. " +',
     'The_section_says_it_assesses_no_safety_or_capacity'),

    ('STR-6 un muro no estructural se cuenta como estructura',
     CMD,
     '                        if (p == null || p.AsInteger() == 0) continue;',
     '                        if (false) continue;',
     'A_structural_wall_is_a_wall_with_the_flag_and_not_the_whole_category'),

    # ---- snapshots: verifiable, sanitised, and never beside the model ----

    ('SNP-1 un snapshot editado se acepta igual',
     'SnapshotStore.cs',
     '            if (!string.Equals(stored, actual, StringComparison.OrdinalIgnoreCase))',
     '            if (false)',
     'A_snapshot_whose_content_was_edited_is_refused_and_not_repaired'),

    ('SNP-2 el hash depende del orden de las claves',
     'SnapshotStore.cs',
     '                foreach (JProperty p in o.Properties().OrderBy(x => x.Name, StringComparer.Ordinal))',
     '                foreach (JProperty p in o.Properties())',
     'The_hash_is_stable_across_key_order'),

    ('SNP-3 un archivo a medias se lee como corrida vacia',
     'SnapshotStore.cs',
     '            if (envelope["content"] == null || envelope["sha256"] == null)',
     '            if (false)',
     'A_file_that_parses_without_an_envelope_is_partial'),

    ('SNP-4 una ruta personal llega al snapshot',
     'SnapshotStore.cs',
     '                foreach (Regex r in Personal) after = r.Replace(after, "<redacted>");',
     '                foreach (Regex r in new Regex[0]) after = r.Replace(after, "<redacted>");',
     'A_personal_path_never_reaches_a_stored_snapshot'),

    ('SNP-5 la sanitizacion no entra en objetos anidados',
     'SnapshotStore.cs',
     '                foreach (JProperty p in o.Properties().ToList()) redacted += Sanitise(p.Value);',
     '                foreach (JProperty p in new JProperty[0]) redacted += Sanitise(p.Value);',
     'Sanitisation_reaches_nested_objects_and_arrays'),

    ('SNP-6 se sanitiza despues de calcular el hash',
     'SnapshotStore.cs',
     '            r.RedactedValues = Sanitise(content);\n            string sha;\n            JObject envelope = Envelope(content, out sha);',
     '            string sha;\n            JObject envelope = Envelope(content, out sha);\n            r.RedactedValues = Sanitise(content);',
     'Sanitisation_happens_before_the_hash_so_the_stored_file_verifies'),

    ('SNP-7 se escribe sin directorio resuelto',
     'SnapshotStore.cs',
     '            if (string.IsNullOrWhiteSpace(directory))',
     '            if (false)',
     'A_write_with_no_directory_is_refused_rather_than_guessed'),

    ('SNP-8 una escritura fallida se reporta como exitosa',
     'SnapshotStore.cs',
     '            if (writeFile != null && !writeFile(r.Path, envelope.ToString(Formatting.Indented)))',
     '            if (false)',
     'A_write_that_fails_is_reported_as_failed_and_never_as_written'),

    ('SNP-9 los snapshots vuelven a vivir junto al modelo',
     'SnapshotStore.cs',
     '            return Path.Combine(dataRoot, "snapshots");',
     '            return dataRoot;',
     'Snapshots_live_under_horizuns_own_root_and_not_beside_the_model'),

    # ---- trends: a smaller sample is not an improvement ----

    ('TRD-1 una cobertura menor se declara mejora',
     'DiagnosticsSnapshot.cs',
     '            if (bounded)\n            {\n                ch.Kind = SnapshotChangeKind.CoverageChanged;',
     '            if (false)\n            {\n                ch.Kind = SnapshotChangeKind.CoverageChanged;',
     'A_LOWER_BOUND_CANNOT_PROVE_AN_IMPROVEMENT'),

    ('TRD-2 una cobertura menor se declara resolucion',
     'DiagnosticsSnapshot.cs',
     '                if (bounded)\n                {\n                    ch.Kind = SnapshotChangeKind.CoverageChanged;',
     '                if (false)\n                {\n                    ch.Kind = SnapshotChangeKind.CoverageChanged;',
     'Nor_can_it_prove_a_resolution'),

    ('TRD-3 un cambio de reglas se confunde con lo incomparable',
     'DiagnosticsSnapshot.cs',
     '                c.RefusalKind = whyNot != null && whyNot.Contains("requirement sets")',
     '                c.RefusalKind = false',
     'A_comparison_refused_for_different_rules_says_so_in_one_word'),

    # ---- health index: coverage decides what the headline may say ----

    ('HLT-1 una mayoria sin medir sigue produciendo puntaje',
     'HealthIndexRules.cs',
     '            if (index.AssessedWeightShare.HasValue && index.AssessedWeightShare.Value < 0.5)',
     '            if (false)',
     'A_majority_unassessed_publishes_no_score_at_all'),

    ('HLT-2 el rango plausible ignora lo no medido',
     'HealthIndexRules.cs',
     '            index.PlausibleLow = allWeight > 0 ? Math.Round(weighted / allWeight, 2) : (double?)null;',
     '            index.PlausibleLow = allWeight > 0 ? Math.Round(weighted / totalWeight, 2) : (double?)null;',
     'A_perfect_score_over_partial_coverage_is_published_with_its_range'),

    ('HLT-3 la cobertura se declara siempre completa',
     'HealthIndexRules.cs',
     '            index.AssessedWeightShare = allWeight > 0 ? Math.Round(totalWeight / allWeight, 4) : (double?)null;',
     '            index.AssessedWeightShare = 1.0;',
     'A_perfect_score_over_partial_coverage_is_published_with_its_range'),

    ('HLT-4 las dimensiones sin medir dejan de nombrarse',
     'HealthIndexRules.cs',
     '                if (d.State != DimensionState.Scored || !d.Score.HasValue) index.Unassessed.Add(d.Dimension);',
     '                if (false) index.Unassessed.Add(d.Dimension);',
     'A_perfect_score_over_partial_coverage_is_published_with_its_range'),

    # ---- federation and foreign content ----

    ('FED-1 un ciclo de vinculos deja de detectarse',
     'FederationContentRules.cs',
     '            if (onPath.Contains(node))',
     '            if (false)',
     'A_link_that_loads_a_link_that_loads_it_back_is_found'),

    ('FED-2 un ciclo se reporta rotado segun por donde se empiece',
     'FederationContentRules.cs',
     '                if (seen.Add(key)) found.Add(Rotate(cycle));',
     '                if (seen.Add(key)) found.Add(cycle);',
     'A_cycle_is_reported_the_same_way_whichever_link_is_walked_first'),

        # FED-3 originally tried to make a deep tree read as a cycle by removing the
    # backtracking. It could not bite: the `at < 0` guard means a stale on-path
    # entry produces a MISSED cycle rather than a false one, so that property is
    # guarded twice and no single edit surfaces it. Replaced with the dedup, which
    # is guarded once and would otherwise report one loop once per member.
    ('FED-3 un ciclo se reporta una vez por cada nodo',
     'FederationContentRules.cs',
     '                if (seen.Add(key)) found.Add(Rotate(cycle));',
     '                seen.Add(key); found.Add(Rotate(cycle));',
     'A_longer_loop_is_found_and_reported_once'),

    ('FED-4 una ruta ausente se cuenta como rota',
     'FederationContentRules.cs',
     '                    ["without_a_path"] = g.Count(p => p.Resolves == null)',
     '                    ["without_a_path"] = 0',
     'A_path_that_does_not_resolve_is_apart_from_having_no_path'),

    ('FED-5 los tipos de contenido se cuentan juntos',
     'FederationContentRules.cs',
     '            foreach (IGrouping<string, ExternalPathFact> g in all.GroupBy(p => p.Kind ?? "(unknown)")',
     '            foreach (IGrouping<string, ExternalPathFact> g in all.GroupBy(p => "all")',
     'Kinds_are_kept_apart_so_a_missing_texture_is_not_a_missing_point_cloud'),

    ('FED-6 un anidamiento ilegible se reporta como cero',
     'FederationContentRules.cs',
     '                ["nested_link_count"] = f.NestedReadable ? (JToken)f.NestedLinkNames.Count : null,',
     '                ["nested_link_count"] = f.NestedLinkNames.Count,',
     'A_link_whose_nesting_could_not_be_read_reports_null_and_not_zero'),

    ('FED-7 los decals se declaran contados en cero',
     'FederationContentRules.cs',
     '            "decals are NOT OBSERVABLE through the API of any supported Revit year',
     '            "decals: 0. Counted through the API of any supported Revit year',
     'Decals_are_declared_unobservable_rather_than_counted_as_zero'),



    ('FED-10 una ruta ausente se trata como rota en el comando',
     CMD,
     '                        f.Resolves = string.IsNullOrWhiteSpace(f.Path) ? (bool?)null : FileExists(f.Path);',
     '                        f.Resolves = FileExists(f.Path);',
     'External_content_separates_a_missing_path_from_no_path_at_all'),

    # ---- scope boxes, per-view north, and a limitation modelled explicitly ----

    ('SCB-1 un scope box asignado sin geometria se declara no asignado',
     'ScopeBoxRules.cs',
     '                if (string.IsNullOrWhiteSpace(ScopeBoxName)) return ScopeBoxState.NotAssigned;',
     '                if (string.IsNullOrWhiteSpace(ScopeBoxName) || GeometryMissing) return ScopeBoxState.NotAssigned;',
     'An_assigned_box_whose_extents_will_not_come_back_keeps_its_assignment'),

    ('SCB-2 una lectura fallida se reporta como no asignado',
     'ScopeBoxRules.cs',
     '                if (!Readable) return ScopeBoxState.Unreadable;',
     '                if (!Readable) return ScopeBoxState.NotAssigned;',
     'A_failed_read_is_unreadable_and_never_not_assigned'),

    ('SCB-3 una caja sin geometria reporta cero en vez de nulo',
     'ScopeBoxRules.cs',
     '            if (!a.HasValue || !b.HasValue) return null;',
     '            if (!a.HasValue || !b.HasValue) return 0.0;',
     'A_box_with_no_readable_geometry_reports_null_spans_and_not_zero'),

    ('SCB-4 se agrupan bajo una caja los duenos sin asignacion',
     'ScopeBoxRules.cs',
     '                if (a == null || a.State == ScopeBoxState.NotAssigned || a.State == ScopeBoxState.Unreadable)\n                    continue;',
     '                if (a == null) continue;',
     'Unassigned_and_unreadable_owners_are_not_grouped_under_a_box'),

    ('SCB-5 se pierde la advertencia de que la geometria es la propia',
     'ScopeBoxRules.cs',
     '            "the extents below are the scope box\'s OWN bounding box, read from the scope box element itself. " +',
     '            "the extents below describe the scope box. " +',
     'The_extents_are_the_scope_boxs_own_and_the_reply_says_so'),

    ('SCB-6 lo ilegible deja de romper la exactitud',
     'ScopeBoxRules.cs',
     '                ["counts_are_exact"] = counts[ScopeBoxState.Unreadable] == 0,',
     '                ["counts_are_exact"] = true,',
     'One_unreadable_assignment_makes_the_counts_inexact'),

    ('SCB-7 la geometria se toma del contenido y no de la caja',
     CMD,
     '                        BoundingBoxXYZ bb = e.get_BoundingBox(null);',
     '                        BoundingBoxXYZ bb = new FilteredElementCollector(doc).FirstElement().get_BoundingBox(null);',
     'Scope_box_extents_are_read_off_the_scope_box_element_itself'),

    ('SHP-1 la posicion compartida se declara observable',
     'CoordinateRules.cs',
     '            "NOT OBSERVABLE. Revit offers no read path from a placed link back to the placement it was created " +',
     '            "Read from the link. Revit offers a read path from a placed link back to the placement it was created " +',
     'Shared_position_is_declared_unobservable_with_the_reason_and_the_refusal'),

    ('SHP-2 se pierde el rechazo a inferirla por similitud de transformadas',
     'CoordinateRules.cs',
     '            "reflection over Revit 2023 through 2027. It is NOT inferred from transform similarity: a link " +',
     '            "reflection over Revit 2023 through 2027. It is inferred from transform similarity: a link " +',
     'Shared_position_is_declared_unobservable_with_the_reason_and_the_refusal'),

    ('FED-11 el anidamiento vuelve a depender de abrir el vinculo',
     CMD,
     '                    foreach (ElementId childId in lt.GetChildIds())',
     '                    foreach (ElementId childId in new List<ElementId>())',
     'Nested_links_come_from_the_type_graph_and_nothing_opens_a_file'),

    ('FED-12 se deja de reportar si el vinculo esta anidado',
     CMD,
     '                try { f.IsNested = lt.IsNestedLink; } catch { f.IsNested = null; }',
     '                f.IsNested = null;',
     'Nesting_does_not_depend_on_the_link_being_loaded'),


    # ---- documentary context: absent is not blank, and nothing is mandatory ----

    ('DOC-1 un campo ausente se reporta como vacio',
     CMD,
     '                if (p == null) { f.Present = false; return f; }',
     '                if (p == null) { f.Present = true; return f; }',
     'The_documentary_read_separates_absent_from_blank_at_the_source'),

    ('DOC-2 sin perfil se declara conforme',
     'DocumentaryContextRules.cs',
     '            if (rule == null)\n            {\n                v.Outcome = ParameterOutcome.RuleNotRequested;',
     '            if (rule == null)\n            {\n                v.Outcome = ParameterOutcome.Present;',
     'With_no_profile_every_field_is_not_requested_and_that_is_not_a_pass'),

    ('DOC-3 se pierde la distincion entre ausente y vacio',
     'DocumentaryContextRules.cs',
     '                Present = fact.Present,',
     '                Present = true,',
     'A_field_that_does_not_exist_is_apart_from_one_that_exists_and_is_blank'),

    ('DOC-4 lo ilegible se trata como legible',
     'DocumentaryContextRules.cs',
     '                Readable = fact.Readable,',
     '                Readable = true,',
     'A_field_that_could_not_be_read_is_neither_absent_nor_blank'),

    ('DOC-5 el guid deja de distinguir homonimos',
     'DocumentaryContextRules.cs',
     '                Guid = fact.Guid,\n                IsShared = fact.Guid != null,',
     '                Guid = null,\n                IsShared = false,',
     'A_field_with_the_right_name_and_the_wrong_guid_does_not_satisfy_a_guid_rule'),

    ('DOC-6 una regla sobre un campo no recolectado se descarta',
     'DocumentaryContextRules.cs',
     '                if (collected.Contains(kv.Key)) continue;',
     '                continue;',
     'A_rule_about_a_field_nothing_collected_is_named_as_this_tools_gap'),

    ('DOC-7 se pierde el aviso de que sin perfil no hay aprobado',
     'DocumentaryContextRules.cs',
     '            "with no documentary profile every field is not_requested, which is NOT a pass. Which fields a " +',
     '            "with no documentary profile the documentary context is clean. Which fields a " +',
     'With_no_profile_every_field_is_not_requested_and_that_is_not_a_pass'),

    ('DOC-8 el valor se juzga como placeholder por su cuenta',
     'DocumentaryContextRules.cs',
     '                ValueAsString = fact.Value,\n                HasValue = !string.IsNullOrEmpty(fact.Value)',
     '                ValueAsString = "TBD",\n                HasValue = true',
     'A_declared_placeholder_is_its_own_outcome_and_not_a_filled_field'),


    # ---- 4D/5D readiness: evidence found, per category, never a score ----

    ('RDY-1 una categoria vacia se declara ausente',
     'DeliveryReadinessRules.cs',
     '            if (m.Population == 0)',
     '            if (false)',
     'A_category_with_no_elements_is_not_assessable_rather_than_absent'),

    ('RDY-2 completo se declara pese a lo ilegible',
     'DeliveryReadinessRules.cs',
     '                v.State = m.Unreadable > 0 ? RoleState.Partial : RoleState.Complete;',
     '                v.State = RoleState.Complete;',
     'Complete_requires_that_nothing_was_unreadable'),

    ('RDY-3 una poblacion ilegible se declara ausente',
     'DeliveryReadinessRules.cs',
     '                v.State = m.Unreadable > 0 ? RoleState.Unreadable : RoleState.NotAssessable;',
     '                v.State = RoleState.Absent;',
     'A_population_that_could_not_be_read_is_unreadable_and_not_absent'),

    ('RDY-4 un rol no requerido se juzga igual',
     'DeliveryReadinessRules.cs',
     '            if (!required)\n            {\n                v.State = RoleState.NotRequired;',
     '            if (false)\n            {\n                v.State = RoleState.NotRequired;',
     'A_role_nobody_required_is_not_required_and_never_absent'),

    ('RDY-5 la cobertura vacia se reporta como cero',
     'DeliveryReadinessRules.cs',
     '                if (Evaluated <= 0) return null;',
     '                if (Evaluated <= 0) return 0.0;',
     'Coverage_is_null_when_nothing_was_evaluated_and_never_zero'),

    ('RDY-6 lo parcial se redondea a completo',
     'DeliveryReadinessRules.cs',
     '            if (judged.All(v => v.State == RoleState.Complete)) return RoleState.Complete;',
     '            if (judged.Any(v => v.State == RoleState.Complete)) return RoleState.Complete;',
     'Found_is_weaker_than_complete_and_says_only_that_something_carries_it'),

    ('RDY-7 lo no evaluable arrastra al rol hacia abajo',
     'DeliveryReadinessRules.cs',
     '            var judged = all.Where(v => v.State != RoleState.NotRequired &&\n                                        v.State != RoleState.NotAssessable).ToList();',
     '            var judged = all;',
     'Categories_that_are_not_assessable_do_not_drag_a_role_down'),

    ('RDY-8 la dimension publica un puntaje',
     'DeliveryReadinessRules.cs',
     '            o["score"] = null;',
     '            o["score"] = 100;',
     'A_dimension_publishes_no_score_because_readiness_is_not_a_scalar'),

    ('RDY-9 se pierde el rechazo a afirmar integracion',
     'DeliveryReadinessRules.cs',
     '            "a parameter carrying a value is not a connection to a schedule or a budget. It is evidence that " +',
     '            "a parameter carrying a value shows a connection to a schedule or a budget. It is evidence that " +',
     'The_reply_refuses_to_claim_an_integration'),

    ('RDY-10 un rol se mide sobre todo el modelo',
     CMD,
     '                foreach (string category in role.Rule.Categories)',
     '                foreach (string category in new[] { "*" })',
     'A_readiness_role_is_measured_only_on_the_categories_it_declares'),

    # ---- classification: a group code is not a leaf ----

    ('CLS-1 un codigo de grupo se acepta como hoja',
     'ClassificationCatalogueRules.cs',
     '            return isLeaf ? CodeStatus.Leaf : CodeStatus.GroupNotTerminal;',
     '            return CodeStatus.Leaf;',
     'A_group_code_is_real_and_still_not_priceable'),

    ('CLS-2 un catalogo ausente se confunde con codigo no encontrado',
     'ClassificationCatalogueRules.cs',
     '            if (catalogue == null || catalogue.Absent) return CodeStatus.CatalogueNotSupplied;',
     '            if (catalogue == null || catalogue.Absent) return CodeStatus.NotInCatalogue;',
     'A_missing_catalogue_is_apart_from_a_code_missing_from_one'),

    ('CLS-3 un catalogo roto se confunde con uno ausente',
     'ClassificationCatalogueRules.cs',
     '            if (!catalogue.Ok) return CodeStatus.CatalogueUnreadable;',
     '            if (!catalogue.Ok) return CodeStatus.CatalogueNotSupplied;',
     'A_broken_catalogue_is_apart_from_a_missing_one'),

    ('CLS-4 un codigo vacio se reporta como ausente del catalogo',
     'ClassificationCatalogueRules.cs',
     '            if (string.IsNullOrWhiteSpace(code)) return CodeStatus.Invalid;',
     '            if (string.IsNullOrWhiteSpace(code)) return CodeStatus.NotInCatalogue;',
     'An_empty_or_blank_code_is_invalid_rather_than_absent_from_the_catalogue'),

    ('CLS-5 la condicion de hoja se infiere de la forma del codigo',
     'ClassificationCatalogueRules.cs',
     '                if (p.Value.Type != JTokenType.Boolean)',
     '                if (false)',
     'Leafness_is_declared_and_never_inferred_from_the_codes_shape'),

    ('CLS-6 un catalogo vacio se acepta y reprueba todo',
     'ClassificationCatalogueRules.cs',
     '            if (c.Codes.Count == 0)',
     '            if (false)',
     'An_empty_catalogue_is_refused_rather_than_failing_every_code'),

    ('CLS-7 un codigo no requerido se juzga igual',
     'ClassificationCatalogueRules.cs',
     '            if (!required) return CodeStatus.NotRequired;',
     '            if (false) return CodeStatus.NotRequired;',
     'A_code_nobody_asked_about_is_not_required_and_never_invalid'),

    ('CLS-8 se pierde el aviso de que no hay catalogo compilado',
     'ClassificationCatalogueRules.cs',
     '            "UniFormat, MasterFormat and every house standard belong to somebody and not to everybody. " +',
     '            "UniFormat and MasterFormat are built in. " +',
     'No_catalogue_is_compiled_in'),


    # ---- guided corrections: proposals only, from a registry, never widened ----

    ('GCP-1 una propuesta amplia su alcance',
     'GuidedCorrectionRules.cs',
     '            if (proposed.Except(found).Any())',
     '            if (false)',
     'A_proposal_may_narrow_its_scope_and_never_widen_it'),

    ('GCP-2 un hallazgo de otro documento se acepta',
     'GuidedCorrectionRules.cs',
     '            if (!string.Equals(finding.DocumentTitle, targetDocument, StringComparison.Ordinal))',
     '            if (false)',
     'A_finding_from_another_document_is_refused_as_unsafe'),

    ('GCP-3 un fingerprint distinto se ignora',
     'GuidedCorrectionRules.cs',
     '            if (!string.Equals(finding.DocumentFingerprint, documentFingerprint, StringComparison.Ordinal))',
     '            if (false)',
     'A_changed_fingerprint_is_refused_because_ids_may_name_other_elements'),

    ('GCP-4 un hallazgo truncado se propone igual',
     'GuidedCorrectionRules.cs',
     '            if (finding.Truncated)',
     '            if (false)',
     'A_truncated_finding_has_an_unknown_scope_and_requires_input'),

    ('GCP-5 un hallazgo resuelto genera propuesta',
     'GuidedCorrectionRules.cs',
     '            if (finding.Resolved)',
     '            if (false)',
     'A_resolved_finding_produces_nothing_and_is_not_an_error'),

    ('GCP-6 una herramienta fuera del registro se improvisa',
     'GuidedCorrectionRules.cs',
     '            if (registry == null || !registry.TryGetValue(finding.FindingType ?? "", out recipe))',
     '            recipe = new CorrectionRecipe { Tool = "horizun_" + finding.FindingType };\n            if (false)',
     'A_finding_with_no_registered_correction_is_unsupported_not_improvised'),

    ('GCP-7 una ambiguedad se resuelve sola',
     'GuidedCorrectionRules.cs',
     '            if (p.Ambiguities.Count > 0)',
     '            if (false)',
     'An_ambiguous_correction_returns_the_options_rather_than_choosing'),

    ('GCP-8 lo no automatizable se propone igual',
     'GuidedCorrectionRules.cs',
     '            if (recipe.CannotAutomateBecause != null)',
     '            if (false)',
     'A_correction_that_cannot_be_automated_says_why_rather_than_guessing'),

    ('GCP-9 una propuesta expirada se declara vigente',
     'GuidedCorrectionRules.cs',
     '            return string.CompareOrdinal(nowUtc, p.ExpiresUtc) > 0;',
     '            return false;',
     'An_expired_proposal_is_recognised_by_comparison_and_not_by_a_clock'),

    ('GCP-10 el dry run deja de venir activado',
     'GuidedCorrectionRules.cs',
     '                ["dry_run"] = true',
     '                ["dry_run"] = false',
     'A_valid_finding_becomes_an_actionable_proposal_that_executes_nothing'),

    # ---- prevention: incomplete coverage may block, never allow ----

    ('PRV-1 una cobertura incompleta permite igual',
     'PreventionGateRules.cs',
     '            if (!input.CoverageComplete)\n            {\n                v.Decision = GateDecision.NotAssessable;',
     '            if (false)\n            {\n                v.Decision = GateDecision.NotAssessable;',
     'Incomplete_coverage_may_block_but_never_allow'),

    ('PRV-2 una operacion no controlada se permite',
     'PreventionGateRules.cs',
     '            if (!input.OperationIsControlled)',
     '            if (false)',
     'An_operation_this_bridge_does_not_control_is_not_assessable_and_not_permission'),

    ('PRV-3 la ausencia de auditoria se lee como limpia',
     'PreventionGateRules.cs',
     '            if (input.AuditSupplied != true)',
     '            if (false)',
     'No_audit_is_not_a_clean_audit'),

    ('PRV-4 un override incompleto se acepta',
     'PreventionGateRules.cs',
     '            if (!o.IsComplete)',
     '            if (false)',
     'An_override_missing_its_signature_is_refused'),

    ('PRV-5 un override de otra operacion sirve igual',
     'PreventionGateRules.cs',
     '            if (!string.Equals(o.Operation, input.Operation, StringComparison.Ordinal))',
     '            if (false)',
     'An_override_for_another_operation_is_not_permission_for_this_one'),

    # Dentro de un plan atomico la confirmacion de CADA hijo se cortocircuita:
    # RequireConfirmation abre con `if (_atomicPlanDepth > 0) return StillTheSame(...)`,
    # que prueba que el documento activo no cambio y no mira token ni plan. Eso es
    # seguro por una sola razon: el plan entero ya se confirmo contra planHash ANTES
    # de entrar. Subir el alcance por encima de la confirmacion deja a cada hijo
    # corriendo sin confirmar y sin nada confirmado en su lugar.
    ('PLN-1 el alcance atomico se entra antes de confirmar el plan',
     '../Commands/ExecutePlanCommand.cs',
     '            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,',
     '            using (DocumentGate.EnterConfirmedAtomicPlan()) { }\n            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,',
     'The_plan_is_confirmed_BEFORE_the_scope_that_disables_child_confirmation_is_entered'),

    ('SHT-17 una regla de lamina se parsea y no la juzga nadie',
     'SheetAnnotationRules.cs',
     '        public int? MaxViewports;',
     '        public int? MaxViewports;\n        public List<string> RequiredScheduleNames;',
     'Every_sheet_rule_field_is_read_where_the_rules_are_judged'),

    ('PRV-6 un override de otro perfil sirve igual',
     'PreventionGateRules.cs',
     '            if (input.ProfileVersion != null && o.ProfileVersion != null &&',
     '            if (false &&',
     'An_override_signed_against_another_profile_is_refused'),

    ('PRV-7 un override expirado se acepta',
     'PreventionGateRules.cs',
     '                if (string.CompareOrdinal(nowUtc, o.ExpiresUtc) > 0)',
     '                if (false)',
     'An_expired_override_is_refused_by_comparison_and_not_by_a_clock'),

    # La comparacion ya no es la unica forma de que un vencimiento no ate: antes
    # se saltaba entera cuando now_utc venia vacio, y now_utc es opcional y lo
    # manda el cliente. Dejar de mandar un campo opcional mantenia viva una
    # anulacion vencida.
    ('PRV-9 un vencimiento sin reloj se acepta en vez de rechazarse',
     'PreventionGateRules.cs',
     '                if (string.IsNullOrEmpty(nowUtc))',
     '                if (false)',
     'An_override_with_an_expiry_and_no_now_utc_is_refused_not_honoured'),

    ('PRV-8 un override cubre hallazgos que no nombra',
     'PreventionGateRules.cs',
     '                if (uncovered.Count > 0)',
     '                if (false)',
     'An_override_that_names_only_some_findings_covers_only_those'),

    # ---- batch: the model list, the sequence it becomes, the report it means ----

    ('BAT-1 un modelo que no se abrio cuenta como limpio',
     'BatchAuditRules.cs',
     '            return outcome == Audited;',
     '            return outcome != NotAssessed;',
     'A_model_whose_close_failed_is_retried_because_it_was_never_a_clean_result'),

    ('BAT-2 el denominador se encoge a los que abrieron',
     'BatchAuditRules.cs',
     '                ["all_models_assessed"] = listed > 0 && audited == listed,',
     '                ["all_models_assessed"] = listed > 0 && audited > 0,',
     'The_aggregate_counts_every_model_listed_and_never_calls_a_partial_sweep_complete'),

    ('BAT-3 un modelo listado y no reportado desaparece',
     'BatchAuditRules.cs',
     '            counts[BatchOutcome.NotAssessed] += unreported;',
     '            counts[BatchOutcome.NotAssessed] += 0;',
     'A_model_listed_and_never_reported_is_counted_as_not_assessed'),

    ('BAT-4 un modelo que abrio y no se audito cuenta como auditado',
     'BatchAuditRules.cs',
     '                else if (audit == null || audit.Status != StepStatus.Succeeded)',
     '                else if (false)',
     'A_model_whose_audit_timed_out_was_opened_and_not_examined'),

    ('BAT-5 un cierre fallido no marca el documento abierto',
     'BatchAuditRules.cs',
     '                if (close != null && close.Status == StepStatus.Failed)',
     '                if (false)',
     'A_close_that_failed_leaves_the_run_saying_a_document_is_open'),

    ('BAT-6 un modelo nunca abierto se reporta como documento abierto',
     'BatchAuditRules.cs',
     '                    r.Why = "this model was never opened. " + NotAssessedMeans;\n                    r.DocumentClosed = true;',
     '                    r.Why = "this model was never opened. " + NotAssessedMeans;\n                    r.DocumentClosed = false;',
     'A_model_that_was_never_opened_is_not_reported_as_a_document_left_open'),

    ('BAT-7 una apertura fallida no distingue de no auditado',
     'BatchAuditRules.cs',
     '                else if (open.Status != StepStatus.Succeeded)',
     '                else if (false)',
     'A_model_that_will_not_open_is_not_opened_and_never_clean'),

    ('BAT-8 el error de la apertura no llega al informe',
     'BatchAuditRules.cs',
     '                    r.Why = open.Error;',
     '                    r.Why = null;',
     'A_model_blocked_by_a_dialog_carries_the_reason_rather_than_a_bare_failure'),

    ('BAT-9 un modelo auditado pierde la referencia a su resultado',
     'BatchAuditRules.cs',
     '                    r.ResultRef = audit.ResultRef;',
     '                    r.ResultRef = null;',
     'An_audited_model_carries_the_reference_to_its_stored_reply'),

    ('BAT-10 una reanudacion repite lo ya auditado',
     'BatchAuditRules.cs',
     '                    .Where(r => r != null && BatchOutcome.IsEvidence(r.Outcome))\n                    .Select(r => r.Id),',
     '                    .Where(r => r != null)\n                    .Select(r => r.Id),',
     'A_resumed_sweep_skips_what_was_audited_and_retries_what_was_not'),

    ('BAT-11 dos modelos comparten id',
     'BatchAuditRules.cs',
     '                if (!seen.Add(m.Id))',
     '                if (false)',
     'Two_models_sharing_an_id_are_refused_before_anything_opens'),

    ('BAT-12 una copia descargada pasa por el modelo cloud',
     'BatchAuditRules.cs',
     '                    if (!string.IsNullOrWhiteSpace(m.LocalPath))\n                    {\n                        p.Code = BatchRefusal.LocalPathAsCloud;',
     '                    if (false)\n                    {\n                        p.Code = BatchRefusal.LocalPathAsCloud;',
     'A_downloaded_copy_is_not_the_cloud_model'),

    ('BAT-13 una identidad cloud que no es GUID se acepta',
     'BatchAuditRules.cs',
     '                    if (!Guid.TryParse(m.CloudProjectGuid.Trim(), out ignored) ||',
     '                    if (false ||',
     'A_cloud_identity_that_is_not_a_guid_is_refused_now_rather_than_at_open_time'),

    ('BAT-14 un barrido vacio no encontro nada malo',
     'BatchAuditRules.cs',
     '            if (list.Count == 0)',
     '            if (false)',
     'An_empty_sweep_is_refused_rather_than_reported_as_finding_nothing'),

    ('BAT-15 un origen desconocido se adivina',
     'BatchAuditRules.cs',
     '                    p.Code = BatchRefusal.UnknownOrigin;',
     '                    p.Ok = true; p.Models = list; return p;\n                    p.Code = BatchRefusal.UnknownOrigin;',
     'An_origin_that_is_neither_local_nor_cloud_is_refused_rather_than_guessed'),

    ('BAT-16 el barrido abre adjunto al central',
     'BatchAuditRules.cs',
     '            if (options != null && !options.Detach)',
     '            if (false)',
     'A_sweep_that_asks_to_open_attached_is_refused'),

    ('BAT-17 un modelo sin titulo esperado se admite',
     'BatchAuditRules.cs',
     '                if (string.IsNullOrWhiteSpace(m.ExpectedTitle))',
     '                if (false)',
     'A_model_that_does_not_say_what_it_should_be_called_is_refused'),

    ('BAT-18 el open generado deja de ser detached',
     'BatchAuditRules.cs',
     '                var open = new JObject { ["detach"] = true };',
     '                var open = new JObject { ["detach"] = false };',
     'Every_generated_open_is_detached_so_the_sweep_has_no_central_to_write_to'),

    ('BAT-19 el modelo cloud se expande a una ruta',
     'BatchAuditRules.cs',
     '                if (m.Origin == ModelOrigin.Cloud)\n                {\n                    open["cloud_project_guid"]',
     '                if (false)\n                {\n                    open["cloud_project_guid"]',
     'A_cloud_model_expands_to_its_typed_guids_and_never_to_a_path'),

    ('BAT-20 la auditoria generada no nombra su documento',
     'BatchAuditRules.cs',
     '                var audit = new JObject { ["target_document"] = m.ExpectedTitle };',
     '                var audit = new JObject();',
     'Every_generated_audit_and_close_names_its_target_document'),

    ('BAT-21 el perfil del modelo no gana sobre el de la corrida',
     'BatchAuditRules.cs',
     '                string profile = string.IsNullOrWhiteSpace(m.ProfileVersion) ? options.ProfileVersion : m.ProfileVersion;',
     '                string profile = options.ProfileVersion;',
     'Each_model_is_judged_by_its_own_profile_and_the_run_default_fills_the_gap'),

    ('BAT-22 el cierre generado deja de ser un cierre',
     'BatchAuditRules.cs',
     '                        ["operation"] = "close",',
     '                        ["operation"] = "save",',
     'A_generated_sweep_names_no_tool_that_could_write_to_a_model'),

    ('BAT-23 un modelo sin id se admite',
     'BatchAuditRules.cs',
     '                if (string.IsNullOrWhiteSpace(m.Id))',
     '                if (false)',
     'A_model_with_no_id_is_refused_because_a_result_could_not_be_attributed'),

    ('BAT-24 el cierre generado no pide activar otro documento',
     'BatchAuditRules.cs',
     '                        ["activate_other"] = true',
     '                        ["activate_other"] = false',
     'Every_generated_close_asks_to_activate_another_document_first'),

    ('GATE-1 el gate aprueba sobre una propiedad no observable',
     '../Commands/AuditModelCommand.cs',
     '                NotMeasured();',
     '                Part(notSharing, linksUnreadable == 0);',
     'The_unobservable_link_position_is_never_published_as_a_measurement_that_ran'),

    # ---- guided corrections: the registry IS the safety model ----

    ('CRG-1 una herramienta de un elemento acepta cuatro',
     'GuidedCorrectionRules.cs',
     '                if (finding.ElementIds.Count != 1)',
     '                if (false)',
     'A_single_element_tool_handed_four_elements_refuses_rather_than_taking_the_first'),

    ('CRG-2 una receta redirige el documento o convierte el ensayo en escritura',
     'GuidedCorrectionRules.cs',
     '                    if (f.Name == "target_document" || f.Name == "dry_run")',
     '                    if (false)',
     'A_recipe_may_not_redirect_the_document_or_turn_a_rehearsal_into_a_write'),

    ('CRG-3 las constantes tipadas de la receta no se aplican',
     'GuidedCorrectionRules.cs',
     '            if (recipe.FixedArguments != null)',
     '            if (false)',
     'An_unpinned_link_becomes_one_actionable_typed_call_per_link'),

    ('CRG-4 el id del elemento no llega al argumento declarado',
     'GuidedCorrectionRules.cs',
     '                p.Arguments[recipe.ElementArgument] = finding.ElementIds[0];',
     '                p.Arguments["element_ids"] = new JArray();',
     'An_unpinned_link_becomes_one_actionable_typed_call_per_link'),

    ('CRG-5 el registro admite python',
     '../Core/CorrectionRegistry.cs',
     '                        Tool = "horizun_manage_links",',
     '                        Tool = "horizun_execute_python",',
     'The_registry_names_no_tool_that_runs_arbitrary_code'),

    ('CRG-6 una entrada nombra herramienta y ademas se declara inautomatizable',
     '../Core/CorrectionRegistry.cs',
     '                        FindingType = AuditCheckNames.InPlaceFamilies,\n                        CannotAutomateBecause =',
     '                        FindingType = AuditCheckNames.InPlaceFamilies,\n                        Tool = "horizun_manage_views",\n                        CannotAutomateBecause =',
     'Every_registry_entry_either_names_a_tool_or_says_why_it_cannot'),

    ('CRG-7 la plantilla deja de ser un argumento requerido',
     '../Core/CorrectionRegistry.cs',
     '                        RequiredArguments = new List<string> { "template_view_id" },',
     '                        RequiredArguments = new List<string>(),',
     'A_view_without_a_template_returns_the_question_rather_than_choosing_a_template'),

    ('CRG-8 el mensaje de cero elementos aconseja lo imposible',
     'GuidedCorrectionRules.cs',
     '                        (finding.ElementIds.Count == 0',
     '                        (false',
     'A_finding_that_names_no_element_says_the_elements_are_missing_not_an_argument'),

    # ---- the audit surfaces, guarded in source ----

    ('AUS-1 el bloque de correcciones se calcula y se descarta',
     '../Commands/AuditModelCommand.cs',
     '                ["corrections"] = corrections,',
     '                ["corrections_omitted"] = corrections,',
     'Both_surfaces_are_read_from_the_request_and_reach_the_reply'),

    ('AUS-2 el veredicto de prevencion no llega a la respuesta',
     '../Commands/AuditModelCommand.cs',
     '                ["prevention"] = prevention,',
     '                ["prevention_omitted"] = prevention,',
     'Both_surfaces_are_read_from_the_request_and_reach_the_reply'),

    ('AUS-3 la superficie de correcciones se declara ejecutada',
     '../Commands/AuditModelCommand.cs',
     '            tally["executed"] = false;',
     '            tally["executed"] = true;',
     'The_correction_surface_says_it_executed_nothing'),

    ('AUS-4 un hallazgo truncado deja de marcarse',
     '../Commands/AuditModelCommand.cs',
     '                bool truncated = total > shown;',
     '                bool truncated = false;',
     'A_truncated_finding_is_marked_truncated_rather_than_corrected_in_part'),

    ('AUS-5 la cobertura del gate viene del llamante',
     '../Commands/AuditModelCommand.cs',
     '                CoverageComplete = checksFailed.Count == 0 && incompleteChecks.Count == 0 &&',
     '                CoverageComplete = true || checksFailed.Count == 0 && incompleteChecks.Count == 0 &&',
     'The_gate_is_fed_this_runs_coverage_rather_than_a_callers_assurance'),

    ('AUS-6 el gate se declara ejecutor',
     '../Commands/AuditModelCommand.cs',
     '            json["enforced"] = false;',
     '            json["enforced"] = true;',
     'The_gate_decides_and_says_it_does_not_enforce'),

    ('AUS-7 una operacion desconocida pasa el gate',
     '../Commands/AuditModelCommand.cs',
     '            if (string.IsNullOrWhiteSpace(operation) || !GatedOperation.All.Contains(operation))',
     '            if (false)',
     'An_operation_the_bridge_cannot_gate_is_refused_rather_than_allowed'),

    ('AUS-8 el registro no viaja con la respuesta',
     '../Commands/AuditModelCommand.cs',
     '            tally["registry"] = CorrectionRegistry.Describe();',
     '            tally["registry"] = null;',
     'Correction_proposals_come_from_the_registry_and_nothing_else'),

    # ---- sequence: read-only by admission, not_run never omitted ----

    ('SEQ-1 una herramienta de escritura entra en la secuencia',
     'JobSequenceRules.cs',
     '                if (!Allowed.Contains(tool, StringComparer.Ordinal))',
     '                if (false)',
     'One_entry_naming_a_writing_tool_refuses_the_whole_submission_and_names_the_index'),

    ('SEQ-2 la negativa devuelve las entradas ya parseadas',
     'JobSequenceRules.cs',
     '            a.Entries.Clear();',
     '            if (a.Ok) a.Entries.Clear();',
     'One_entry_naming_a_writing_tool_refuses_the_whole_submission_and_names_the_index'),

    ('SEQ-3 document_session se admite en cualquier operacion',
     'JobSequenceRules.cs',
     '                    if (!string.Equals(op, "close", StringComparison.Ordinal))',
     '                    if (false)',
     'Document_session_is_admissible_only_for_close'),

    ('SEQ-4 dos pasos comparten clave',
     'JobSequenceRules.cs',
     '                if (!keys.Add(key))',
     '                if (false)',
     'Duplicate_keys_are_refused_because_steps_are_reported_by_key'),

    ('SEQ-5 tool y sequence conviven y una gana en silencio',
     'JobSequenceRules.cs',
     '            if (hasToolShape)',
     '            if (false)',
     'A_submission_carrying_both_a_tool_and_a_sequence_is_refused_rather_than_resolved'),

    ('SEQ-6 una secuencia mas larga que el tope se trunca',
     'JobSequenceRules.cs',
     '            if (sequence.Count > MaxEntries)',
     '            if (false)',
     'A_sequence_beyond_the_cap_is_refused_rather_than_truncated'),

    ('SEQ-7 los pasos posteriores a un fallo se omiten',
     'JobSequenceRules.cs',
     '                if (e.Status == StepStatus.Queued || e.Status == StepStatus.Running)',
     '                if (false)',
     'A_sequence_whose_third_step_fails_leaves_four_and_five_not_run_and_the_job_failed'),

    ('SEQ-8 un paso running al cerrar el registro cuenta como exito',
     'JobSequenceRules.cs',
     '                if (e.Status == StepStatus.Queued || e.Status == StepStatus.Running)',
     '                if (e.Status == StepStatus.Queued)',
     'A_step_still_marked_running_when_the_record_settles_never_becomes_succeeded'),

    ('SEQ-9 una secuencia con un paso no ejecutado se reporta ok',
     'JobSequenceRules.cs',
     '            if (all.Any(e => e.Status != StepStatus.Succeeded)) return "failed";',
     '            return "ok";\n            if (all.Any(e => e.Status != StepStatus.Succeeded)) return "failed";',
     'Only_a_sequence_whose_every_step_succeeded_is_ok'),

    ('SEQ-10 una secuencia vacia se admite',
     'JobSequenceRules.cs',
     '            if (sequence == null || sequence.Count == 0)',
     '            if (sequence == null)',
     'An_empty_sequence_is_refused'),

    ('SEQ-11 un paso sin clave se admite',
     'JobSequenceRules.cs',
     '                if (string.IsNullOrWhiteSpace(key)) return Refuse(a, At(i, "has no \'key\'."));',
     '                if (false) return Refuse(a, At(i, "has no \'key\'."));',
     'An_entry_without_a_key_or_arguments_is_refused'),

    # ---- the sweep runner in Dispatcher.cs, guarded in source ----

    ('RUN-1 el inicio del paso se registra despues de correrlo',
     'Dispatcher.cs',
     '                    // BEFORE the step, not after: see the header.\n                    try { work.Record.Step(step.Key, step.Tool, StepStatus.Running, null, null); } catch { }\n\n                    CommandResult r = RunOneSequenceStep(app, step);',
     '                    CommandResult r = RunOneSequenceStep(app, step);\n                    // BEFORE the step, not after: see the header.\n                    try { work.Record.Step(step.Key, step.Tool, StepStatus.Running, null, null); } catch { }',
     'A_step_records_its_start_before_it_runs'),

    ('RUN-2 la secuencia no se detiene en el primer fallo',
     'Dispatcher.cs',
     '                    if (step.Status == StepStatus.Failed) break;',
     '                    if (step.Status == StepStatus.Failed) { }',
     'Execution_stops_at_the_first_failure_and_the_rest_are_settled_to_not_run'),

    ('RUN-3 los cierres de un barrido detenido no corren',
     'Dispatcher.cs',
     '            if (stopped) RunPendingCloses(app, work, steps, stoppedAt);',
     '            if (false) RunPendingCloses(app, work, steps, stoppedAt);',
     'The_closes_of_a_stopped_sweep_still_run'),

    ('RUN-4 la limpieza reintenta lecturas ademas de cierres',
     'Dispatcher.cs',
     '                    if (step.Tool != "horizun_document_session") continue;',
     '                    if (false) continue;',
     'The_closes_of_a_stopped_sweep_still_run'),

    ('RUN-10 la limpieza cierra un documento que este barrido nunca abrio',
     'Dispatcher.cs',
     '                    if (!ownOpenSucceeded) continue;',
     '                    if (false) continue;',
     'The_closes_of_a_stopped_sweep_still_run'),

    ('RUN-5 el estado terminal deja de venir de la regla compartida',
     'Dispatcher.cs',
     '            string terminal = JobSequenceRules.TerminalStatus(steps);',
     '            string terminal = "ok";',
     'The_terminal_status_of_a_sweep_comes_from_the_shared_rule'),

    ('RUN-6 los permisos no se revisan al correr el paso',
     'Dispatcher.cs',
     '            if (contract != null && !Settings.IsToolAllowed(contract, out permissionReason))\n                return CommandResult.Fail(permissionReason + " This sequence step did not run.");',
     '            if (false)\n                return CommandResult.Fail(permissionReason + " This sequence step did not run.");',
     'Permissions_are_checked_again_when_a_step_runs'),

    ('RUN-7 la rama de secuencia deja de alcanzarse',
     'Dispatcher.cs',
     '            if (work.Sequence != null && work.Sequence.Count > 0) { RunSequence(app, work); return; }',
     '            if (false) { RunSequence(app, work); return; }',
     'The_sweep_runner_exists_and_is_reached_from_the_async_pump'),

    ('RUN-8 el lote se expande sin las reglas compartidas',
     '../Commands/SubmitJobCommand.cs',
     '            return BatchAuditRules.ToSequence(plan, options);',
     '            return new JArray();',
     'The_submission_path_expands_a_model_list_through_the_shared_rules'),

    ('RUN-9 una lista de modelos rechazada sigue a la cola',
     '../Commands/SubmitJobCommand.cs',
     '                if (sequence == null) return expansionRefusal;',
     '                if (false) return expansionRefusal;',
     'A_refused_model_list_queues_nothing'),

]

def run(test_filter):
    try:
        out = subprocess.run(
            ['dotnet', 'test', 'tests/Horizun.Core.Tests/Horizun.Core.Tests.csproj',
             '--filter', 'FullyQualifiedName~' + test_filter, '-v', 'q', '--nologo'],
            cwd=ROOT, capture_output=True, text=True, timeout=900).stdout
    except Exception as ex:
        return 'ERROR(' + type(ex).__name__ + ')'
    if 'Failed!' in out:
        return 'FAIL'
    if 'error' in out.lower() and 'Passed!' not in out:
        return 'FAIL'
    if 'Passed!' in out:
        return 'PASS'
    return 'UNKNOWN'


def write_text(path, text):
    """Write with retries and VERIFY by reading back."""
    last = None
    for attempt in range(6):
        try:
            io.open(path, 'w', encoding='utf-8', newline='\n').write(text)
            if io.open(path, encoding='utf-8').read() == text:
                return None
            last = 'read-back did not match'
        except Exception as ex:
            last = '%s: %s' % (type(ex).__name__, ex)
        time.sleep(0.25 * (attempt + 1))
    return last


def restore_or_die(path, original, label):
    err = write_text(path, original)
    if err is None:
        return
    rescue = path + '.ORIGINAL'
    try:
        io.open(rescue, 'w', encoding='utf-8', newline='\n').write(original)
    except Exception:
        rescue = '(could not be written)'
    sys.stdout.write('\n' + '=' * 78 + '\n')
    sys.stdout.write('THE TREE IS STILL MUTATED. Stopping.\n')
    sys.stdout.write('  mutation : %s\n  file     : %s\n  error    : %s\n  original : %s\n'
                     % (label, path, err, rescue))
    sys.stdout.write('=' * 78 + '\n')
    sys.stdout.flush()
    sys.exit(9)


def preflight():
    """Refuse to run a ledger whose entries cannot possibly mean anything.

    THE MISTAKE THIS EXISTS FOR, made three times before it was written: a
    mutation edits code the Core test project never compiles, and its guarding
    test is an ordinary behavioural test that therefore never runs it. The
    mutation cannot change the test's outcome, so the ledger reports VACUOUS -
    after a full run.

    THE CRITERION IS LINKAGE, NOT THE FOLDER NAME. The first version of this
    check asked whether the path contained "Commands/", which named the symptom:
    Dispatcher.cs lives in Core/, is just as unreachable from a Core-only test,
    and sailed straight past it. What actually decides it is whether the file
    appears as a <Compile Include> in Horizun.Core.Tests.csproj. If it does not,
    only a test that READS ITS SOURCE can bite, and the pre-flight demands one
    that mentions that file by name.

    Checked before the baseline, because the whole point is not to spend the run
    finding out.
    """
    problems = []
    tests_dir = os.path.join(ROOT, 'tests', 'Horizun.Core.Tests')

    # WHAT THE CORE TESTS ACTUALLY COMPILE. Anything outside this list is code a
    # behavioural test cannot execute, however convincing its name.
    csproj = os.path.join(tests_dir, 'Horizun.Core.Tests.csproj')
    linked = set()
    if os.path.exists(csproj):
        for line in io.open(csproj, encoding='utf-8'):
            if '<Compile Include=' in line:
                linked.add(os.path.basename(line.split('"')[1].replace('\\', '/')))

    # Every test file, indexed by the source files it reads. A mutation in
    # unlinked code needs a guard that names that file - discovered rather than
    # listed, so a new source-reading test counts the day it is written.
    test_text = {}
    for name in sorted(os.listdir(tests_dir)) if os.path.isdir(tests_dir) else []:
        if name.endswith('.cs'):
            test_text[name] = io.open(os.path.join(tests_dir, name), encoding='utf-8').read()

    seen_ids = set()
    for label, rel, find, replace, test_filter in MUTATIONS:
        mid = label.split(' ')[0]
        if mid in seen_ids:
            problems.append('%s: duplicate mutation id' % mid)
        seen_ids.add(mid)

        full = os.path.normpath(os.path.join(ROOT, CORE + rel))
        if not os.path.exists(full):
            problems.append('%s: file not found (%s)' % (mid, rel))
            continue

        text = io.open(full, encoding='utf-8').read()
        n = text.count(find)
        if n == 0:
            problems.append('%s: anchor not found' % mid)
        if find == replace:
            problems.append('%s: replacement is identical to the anchor' % mid)

        # A mutation in code the Core tests do not COMPILE can only bite through a
        # guard that reads that file's source, and that guard must name the file.
        basename = os.path.basename(rel)
        if basename not in linked:
            guarded = any(test_filter in body and basename in body
                          for body in test_text.values())
            if not guarded:
                problems.append(
                    '%s: mutates %s, which the Core test project does not compile, and its '
                    'test "%s" is in no test file that reads that source. Nothing executes '
                    'the mutated code, so this can only come back VACUOUS.'
                    % (mid, rel, test_filter))

    if problems:
        print('PREFLIGHT REFUSED THIS LEDGER:')
        for p in problems:
            print('  ' + p)
        return False
    print('preflight: %d mutations, anchors resolve, unlinked-source entries have naming guards.'
          % len(MUTATIONS))
    sys.stdout.flush()
    return True


def main():
    # ONE green run up front. If the suite is not green, no mutation below can
    # mean anything - a test that already fails "detects" every mutation.
    if not preflight():
        return 3

    print('establishing the baseline (one full run) ...')
    sys.stdout.flush()
    baseline = run('Horizun.Core.Tests')
    if baseline != 'PASS':
        print('THE SUITE IS NOT GREEN (%s). Nothing was mutated: a failing test '
              'appears to detect every mutation.' % baseline)
        return 2
    print('baseline green.')
    sys.stdout.flush()

    results = []
    for label, rel, find, replace, test_filter in MUTATIONS:
        full = os.path.normpath(os.path.join(ROOT, CORE + rel))
        original = io.open(full, encoding='utf-8').read()

        if find not in original:
            results.append((label, 'ANCHOR-MISSING', test_filter))
            print('%-56s %s' % (label[:56], 'ANCHOR-MISSING'))
            sys.stdout.flush()
            continue

        # The "before" run used to happen per mutation, doubling the cost of the
        # whole ledger for a fact that is the same every time: the suite is green.
        # It is established ONCE, at the start, and a mutation whose test was
        # already failing is caught there rather than 56 times.
        before = 'PASS'

        err = write_text(full, original.replace(find, replace, 1))
        if err is not None:
            restore_or_die(full, original, label)
            results.append((label, 'NOT-APPLIED', test_filter))
            continue

        try:
            after = run(test_filter)
        finally:
            restore_or_die(full, original, label)

        verdict = ('BITES' if (before == 'PASS' and after == 'FAIL')
                   else 'VACUOUS' if after == 'PASS'
                   else 'INCONCLUSIVE(' + before + '->' + after + ')')
        results.append((label, verdict, test_filter))
        print('%-56s %s' % (label[:56], verdict))
        sys.stdout.flush()

    print()
    bites = sum(1 for _, v, _ in results if v == 'BITES')
    print('MUERDEN: %d / %d' % (bites, len(results)))
    for label, verdict, test in results:
        if verdict != 'BITES':
            print('  NO MUERDE: %s -> %s (%s)' % (label, verdict, test))
    return 0 if bites == len(results) else 1


if __name__ == '__main__':
    sys.exit(main())
