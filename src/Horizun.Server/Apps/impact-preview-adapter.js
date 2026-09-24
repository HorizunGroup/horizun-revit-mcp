/*
 * Horizun impact preview - the payload adapter. Original Horizun code.
 *
 * Pure functions only: no DOM, no network, no clock. The app inlines this file and
 * tests/Horizun.Server.Tests/ImpactPreview/adapter.test.js runs it under node, so the
 * rules that decide what a click will send are proved without a browser.
 *
 * THE CONTRACT THIS FILE KEEPS. A confirmation_token approves ONE request: every one of
 * the five tools folds the field this adapter narrows (writes, element_ids, ids,
 * protect_ids, operations, elements) into its plan hash. A reduced request therefore
 * cannot spend the token of the full one, and this adapter never tries: excluding
 * anything produces a NEW REHEARSAL request (dry_run true, no token, no idempotency
 * key). Only the arguments of the rehearsal that is on screen are ever turned into an
 * apply, and only with that rehearsal's own token.
 */
(function (root) {
  'use strict';

  var TOOLS = {
    horizun_write_params_verified: { label: 'Write parameters', noun: 'write' },
    horizun_set_keynote: { label: 'Set keynote', noun: 'target' },
    horizun_delete_verified: { label: 'Delete', noun: 'element' },
    horizun_transform_elements: { label: 'Transform elements', noun: 'element' },
    horizun_create_elements: { label: 'Create elements', noun: 'element' }
  };

  function has(o, k) { return o !== null && typeof o === 'object' && Object.prototype.hasOwnProperty.call(o, k); }
  function isInt(v) { return typeof v === 'number' ? isFinite(v) && Math.floor(v) === v : typeof v === 'string' && /^-?[0-9]+$/.test(v); }
  function toInt(v) { return typeof v === 'number' ? v : parseInt(v, 10); }
  function clone(v) { return v === undefined ? undefined : JSON.parse(JSON.stringify(v)); }

  /** A value as a person reads it. JSON-encoded strings (write_params' before) are shown unquoted. */
  function show(v) {
    if (v === undefined || v === null) return '';
    if (typeof v === 'string') return v;
    if (typeof v === 'object') return JSON.stringify(v);
    return String(v);
  }
  function unjson(v) {
    if (typeof v !== 'string') return show(v);
    if (v.length > 1 && v.charAt(0) === '"' && v.charAt(v.length - 1) === '"') {
      try { return String(JSON.parse(v)); } catch (e) { return v; }
    }
    return v;
  }

  /** Which of the five tools produced this payload. The host's toolInfo wins; the shape is the fallback. */
  function detectTool(payload, hint) {
    if (hint && has(TOOLS, hint)) return hint;
    if (!payload || typeof payload !== 'object') return null;
    if (has(payload, 'writes_planned') && has(payload, 'on_failure_if_run')) return 'horizun_write_params_verified';
    if (has(payload, 'keynote') && Array.isArray(payload.targets)) return 'horizun_set_keynote';
    if ((payload.mode === 'ids' || payload.mode === 'purge_unused') &&
        (has(payload, 'would_delete_total') || has(payload, 'purge_supported') || has(payload, 'requested_total')))
      return 'horizun_delete_verified';
    if (has(payload, 'valid_operations')) return 'horizun_transform_elements';
    if (has(payload, 'validation_level') || (has(payload, 'valid') && has(payload, 'invalid'))) return 'horizun_create_elements';
    return null;
  }

  function isRehearsal(payload) {
    if (!payload) return false;
    if (payload.dry_run === true || payload.mode === 'dry_run') return true;
    if (payload.application && payload.application.state === 'rehearsed') return true;
    if (payload.transaction_status === 'not_started' && typeof payload.confirmation_token === 'string') return true;
    return typeof payload.confirmation_token === 'string' && payload.dry_run !== false;
  }

  function previewRows(payload) {
    var p = payload && payload.change_preview;
    return p && Array.isArray(p.rows) ? p.rows : [];
  }

  function row(key, fields) {
    var r = {
      key: key, elementId: null, label: '', category: '', level: '', type: '', action: '',
      changes: [], excludable: false, whyNot: '', note: ''
    };
    for (var k in fields) if (has(fields, k)) r[k] = fields[k];
    return r;
  }

  // ---- Per-tool readers: payload -> rows. -------------------------------------------

  function rowsWriteParams(payload, args) {
    var categories = {};
    previewRows(payload).forEach(function (p) {
      if (p && p.element_id !== null && p.element_id !== undefined) categories[String(p.element_id)] = p.category || '';
    });
    var tabular = !!(args && args.tabular_source) || !!payload.tabular;
    var writes = args && Array.isArray(args.writes) ? args.writes : null;
    var source = Array.isArray(payload.rows) ? payload.rows : [];
    return source.map(function (w) {
      var index = w.index;
      var excludable = !tabular && writes !== null && isInt(index) && toInt(index) >= 0 && toInt(index) < writes.length;
      var id = w.target_id === null || w.target_id === undefined ? null : String(w.target_id);
      return row('w:' + index, {
        elementId: id,
        label: (w.target_name || (w.target_kind === 'project_info' ? 'Project Information' : '')) + (id ? ' [' + id + ']' : ''),
        category: (id && categories[id]) || (w.target_kind === 'project_info' ? 'Project Information' : ''),
        type: w.target_kind || '',
        action: w.error ? 'unresolved' : 'modify',
        changes: [{ name: w.parameter || '', before: unjson(w.before), after: unjson(w.requested) }],
        excludable: excludable,
        whyNot: excludable ? '' : (tabular ? 'rows expanded from a CSV cannot be excluded one by one'
                                           : 'the original writes[] were not received from the host'),
        note: w.error ? String(w.error) : (w.elements_affected > 1 ? w.elements_affected + ' elements carry this value' : '')
      });
    });
  }

  function rowsSetKeynote(payload, args) {
    var ids = args && Array.isArray(args.element_ids) ? args.element_ids.map(String) : null;
    var byType = {};
    previewRows(payload).forEach(function (p) { if (p && p.type) (byType[p.type] = byType[p.type] || []).push(p); });
    return (payload.targets || []).map(function (t) {
      var requested = (t.requested_elements || []).map(String);
      var excludable = ids !== null && requested.length > 0 && requested.every(function (r) { return ids.indexOf(r) >= 0; });
      var match = byType[t.target_name];
      return row('t:' + t.target_id, {
        elementId: t.target_id === undefined ? null : String(t.target_id),
        label: (t.target_name || '') + ' [' + t.target_id + ']',
        category: match && match.length === 1 ? (match[0].category || '') : '',
        type: t.writes_to || '',
        action: 'modify',
        changes: [{ name: t.parameter || 'Keynote', before: show(t.current_keynote), after: show(payload.keynote) }],
        requested: requested,
        excludable: excludable,
        whyNot: excludable ? '' : 'the ids this target came from were not received from the host',
        note: (t.elements_affected !== undefined ? t.elements_affected + ' element(s) re-coded' : '') +
              (t.collateral_elements > 0 ? ', ' + t.collateral_elements + ' you did not name' : '')
      });
    });
  }

  function rowsDelete(payload, args) {
    var mode = (args && args.mode) || payload.mode;
    return previewRows(payload).map(function (p) {
      var s = p.captured_state || {};
      var raw = s.raw_id;
      var requested = s.role === 'requested';
      var excludable = requested && isInt(raw) && !!args &&
        (mode === 'purge_unused' || (Array.isArray(args.ids) && args.ids.map(String).indexOf(String(raw)) >= 0));
      return row('d:' + (isInt(raw) ? raw : p.unique_id), {
        elementId: isInt(raw) ? String(raw) : (p.element_id === null || p.element_id === undefined ? null : String(p.element_id)),
        label: (p.type || '') + (isInt(raw) ? ' [' + raw + ']' : ''),
        category: p.category || '',
        type: p.type || '',
        action: p.action || '',
        changes: [{ name: 'fate', before: 'exists', after: p.action === 'delete' ? 'deleted' : (s.verdict || s.confirmed_in_preview || 'kept') }],
        excludable: excludable,
        whyNot: excludable ? '' : (requested ? 'the original request was not received from the host'
                                             : 'deleted as a consequence of id ' + (s.parent_raw_id || '?') + ': exclude that one instead'),
        note: s.role === 'cascade' ? 'cascade of ' + (s.parent_raw_id || '?') : (s.reason || '')
      });
    });
  }

  function rowsTransform(payload, args) {
    var ids = {};
    if (args && Array.isArray(args.operations))
      args.operations.forEach(function (op) { (op && op.element_ids || []).forEach(function (i) { ids[String(i)] = true; }); });
    return previewRows(payload).map(function (p) {
      var before = p.captured_state || {}, after = p.proposed_values || {};
      var changes = [{ name: 'operation', before: '', after: show(after.operation || before.operation) }];
      if (has(after, 'type_id')) changes.push({ name: 'type_id', before: show(before.type_id), after: show(after.type_id) });
      if (has(after, 'pinned')) changes.push({ name: 'pinned', before: show(before.pinned), after: show(after.pinned) });
      var id = p.element_id === null || p.element_id === undefined ? null : String(p.element_id);
      var excludable = id !== null && has(ids, id);
      return row('e:' + (id || p.unique_id), {
        elementId: id, label: (p.type || '') + (id ? ' [' + id + ']' : ''),
        category: p.category || '', type: p.type || '', action: p.action || '', changes: changes,
        excludable: excludable, whyNot: excludable ? '' : 'the original operations were not received from the host'
      });
    });
  }

  function rowsCreate(payload, args) {
    var elements = args && Array.isArray(args.elements) && !args.tabular_source ? args.elements : null;
    return previewRows(payload).map(function (p) {
      var m = /^create:([0-9]+)$/.exec(p.unique_id || '');
      var index = m ? parseInt(m[1], 10) : null;
      var s = p.captured_state || {};
      var excludable = elements !== null && index !== null && index < elements.length;
      return row('c:' + (index !== null ? index : p.unique_id), {
        label: '#' + (index !== null ? index + 1 : '?') + ' ' + (p.category || ''),
        category: p.category || '', level: s.level || '', type: p.type || '', action: 'create',
        changes: [{ name: 'element', before: '', after: (p.category || '') + (p.type ? ' : ' + p.type : '') }],
        excludable: excludable,
        whyNot: excludable ? '' : (args && args.tabular_source ? 'rows expanded from a CSV cannot be excluded one by one'
                                                              : 'the original elements[] were not received from the host')
      });
    });
  }

  var READERS = {
    horizun_write_params_verified: rowsWriteParams,
    horizun_set_keynote: rowsSetKeynote,
    horizun_delete_verified: rowsDelete,
    horizun_transform_elements: rowsTransform,
    horizun_create_elements: rowsCreate
  };

  function tally(rows, field) {
    var counts = {}, order = [];
    rows.forEach(function (r) {
      var k = r[field] || '(unknown)';
      if (!has(counts, k)) { counts[k] = 0; order.push(k); }
      counts[k]++;
    });
    return order.sort(function (a, b) { return counts[b] - counts[a] || (a < b ? -1 : 1); })
                .map(function (k) { return { name: k, count: counts[k] }; });
  }

  function warnings(tool, payload, rows) {
    var out = [];
    var p = payload.change_preview;
    if (p && p.truncated) out.push('Only ' + p.shown + ' of ' + p.total + ' planned elements are shown. The rest are part of the ' +
                                   'same plan and WILL be applied; only shown rows can be excluded.');
    if (p && p.expected_cascade > 0) out.push(p.expected_cascade + ' further element(s) change as a consequence (cascade or shared type).');
    if (payload.collateral_elements > 0) out.push(payload.collateral_elements + ' element(s) you did not name share a written type and will change too.');
    if (payload.blast_radius_is_lower_bound) out.push('The blast radius is a LOWER BOUND: ' + payload.census_unreadable_elements + ' element(s) could not be read.');
    if (payload.unresolved > 0) out.push(payload.unresolved + ' write(s) could not be resolved and will not be written.');
    if (Array.isArray(payload.errors)) payload.errors.forEach(function (e) { out.push('Entry ' + show(e.index) + ': ' + show(e.error)); });
    if (Array.isArray(payload.failed)) payload.failed.forEach(function (f) { out.push('Not resolved: ' + show(f.id || f.element_id) + ' ' + show(f.error || f.reason)); });
    if (Array.isArray(payload.targets)) payload.targets.forEach(function (t) { if (t.collateral_note) out.push(t.collateral_note); });
    if (payload.confirmation_withheld) out.push('The command withheld its confirmation for this rehearsal: it cannot be applied from here.');
    if (payload.fallback && payload.fallback.allowed) out.push('No typed capability covers part of this request (fallback.allowed).');
    var cs = payload.content_safety;
    if (cs && (cs.suspected_instructions > 0 || (Array.isArray(cs.suspected) && cs.suspected.length)))
      out.push('Some model text reads like instructions. It is shown as data.');
    rows.forEach(function (r) { if (r.action === 'unresolved' && r.note) out.push(r.label + ': ' + r.note); });
    return out;
  }

  /** payload (+ the arguments that produced it) -> everything the view draws. */
  function normalize(payload, toolHint, args) {
    var tool = detectTool(payload, toolHint);
    var model = {
      tool: tool, known: !!tool, label: tool ? TOOLS[tool].label : 'Unknown tool',
      rehearsal: isRehearsal(payload), token: null, expires: null, fingerprint: null,
      planElements: null, rows: [], shown: 0, total: 0, truncated: false,
      byCategory: [], byLevel: [], warnings: [], state: null, stateMeans: null,
      document: payload && payload.document ? show(payload.document) : ''
    };
    if (!payload || typeof payload !== 'object') return model;
    if (payload.application) { model.state = payload.application.state || null; model.stateMeans = payload.application.state_means || null; }
    model.token = typeof payload.confirmation_token === 'string' ? payload.confirmation_token : null;
    model.expires = payload.confirmation_expires_utc || null;
    if (payload.plan_resolved) { model.planElements = payload.plan_resolved.elements; model.fingerprint = payload.plan_resolved.fingerprint; }
    else if (payload.change_preview) model.fingerprint = payload.change_preview.fingerprint;
    if (!tool) return model;
    model.rows = READERS[tool](payload, args || null);
    var p = payload.change_preview;
    model.shown = model.rows.length;
    model.total = p && typeof p.total === 'number' ? Math.max(p.total, model.rows.length) : model.rows.length;
    model.truncated = !!(p && p.truncated) || !!payload.rows_truncated;
    model.byCategory = tally(model.rows, 'category');
    model.byLevel = model.rows.some(function (r) { return r.level; }) ? tally(model.rows, 'level') : [];
    model.warnings = warnings(tool, payload, model.rows);
    return model;
  }

  /**
   * The request for a NEW rehearsal without the excluded rows. Never an apply: the old
   * token is removed with the old idempotency key, and dry_run is forced true.
   * -> { ok, arguments, removed, reason }
   */
  function subsetArguments(model, args, excludedKeys) {
    if (!model || !model.tool) return { ok: false, reason: 'This payload is not one the preview can narrow.' };
    if (!args || typeof args !== 'object') return { ok: false, reason: 'The host did not send the original arguments, so a narrower request cannot be built.' };
    var excluded = {};
    (excludedKeys || []).forEach(function (k) { excluded[k] = true; });
    var chosen = model.rows.filter(function (r) { return excluded[r.key]; });
    var blocked = chosen.filter(function (r) { return !r.excludable; });
    if (blocked.length) return { ok: false, reason: blocked[0].label + ' cannot be excluded: ' + blocked[0].whyNot };
    if (!chosen.length) return { ok: false, reason: 'Nothing is excluded.' };

    var a = clone(args);
    delete a.confirmation_token; delete a.idempotency_key;
    a.dry_run = true;
    var removed = 0;

    switch (model.tool) {
      case 'horizun_write_params_verified': {
        var drop = {};
        chosen.forEach(function (r) { drop[r.key.substring(2)] = true; });
        a.writes = a.writes.filter(function (w, i) { if (drop[String(i)]) { removed++; return false; } return true; });
        if (!a.writes.length) return { ok: false, reason: 'Excluding every write leaves nothing to apply.' };
        break;
      }
      case 'horizun_set_keynote': {
        var dropIds = {};
        // A keynote target is a TYPE (or one instance): every id the caller named for it
        // leaves together, because leaving one behind would still re-code the type.
        chosen.forEach(function (r) { (r.requested || []).forEach(function (i) { dropIds[i] = true; }); });
        a.element_ids = a.element_ids.filter(function (i) { if (dropIds[String(i)]) { removed++; return false; } return true; });
        if (!a.element_ids.length) return { ok: false, reason: 'Excluding every target leaves nothing to apply.' };
        break;
      }
      case 'horizun_delete_verified': {
        var ids = chosen.map(function (r) { return toInt(r.elementId); });
        if ((a.mode || 'ids') === 'purge_unused') {
          var prot = Array.isArray(a.protect_ids) ? a.protect_ids.slice() : [];
          ids.forEach(function (i) { if (prot.map(String).indexOf(String(i)) < 0) { prot.push(i); removed++; } });
          a.protect_ids = prot;
        } else {
          var gone = {};
          ids.forEach(function (i) { gone[String(i)] = true; });
          a.ids = a.ids.filter(function (i) { if (gone[String(i)]) { removed++; return false; } return true; });
          if (!a.ids.length) return { ok: false, reason: 'Excluding every id leaves nothing to delete.' };
        }
        break;
      }
      case 'horizun_transform_elements': {
        var out = {};
        chosen.forEach(function (r) { out[r.elementId] = true; });
        a.operations = a.operations.map(function (op) {
          var o = clone(op);
          o.element_ids = (op.element_ids || []).filter(function (i) { if (out[String(i)]) { removed++; return false; } return true; });
          return o;
        }).filter(function (op) { return op.element_ids.length > 0; });
        if (!a.operations.length) return { ok: false, reason: 'Excluding every element leaves nothing to transform.' };
        break;
      }
      case 'horizun_create_elements': {
        var skip = {};
        chosen.forEach(function (r) { skip[r.key.substring(2)] = true; });
        a.elements = a.elements.filter(function (e, i) { if (skip[String(i)]) { removed++; return false; } return true; });
        if (!a.elements.length) return { ok: false, reason: 'Excluding every element leaves nothing to create.' };
        break;
      }
      default:
        return { ok: false, reason: 'Unsupported tool.' };
    }
    return { ok: true, arguments: a, removed: removed };
  }

  /**
   * The apply for the rehearsal ON SCREEN: its own arguments, its own token, a fresh
   * idempotency key. Refuses anything that is not a rehearsal carrying a token.
   */
  function applyArguments(model, rehearsedArgs, idempotencyKey) {
    if (!model || !model.rehearsal) return { ok: false, reason: 'What is shown is not a rehearsal.' };
    if (!model.token) return { ok: false, reason: 'This rehearsal carries no confirmation_token, so nothing can be applied from it.' };
    if (!rehearsedArgs || typeof rehearsedArgs !== 'object') return { ok: false, reason: 'The arguments of this rehearsal are unknown, so its apply cannot be built.' };
    if (!idempotencyKey) return { ok: false, reason: 'An idempotency key is required.' };
    var a = clone(rehearsedArgs);
    a.dry_run = false;
    a.confirmation_token = model.token;
    a.idempotency_key = idempotencyKey;
    return { ok: true, arguments: a };
  }

  /** After a narrowed rehearsal: did every excluded row actually leave the plan? */
  function checkNarrowed(previous, next, excludedKeys) {
    var problems = [];
    var still = next.rows.filter(function (r) { return (excludedKeys || []).indexOf(r.key) >= 0 && r.key.charAt(0) !== 'c' && r.key.charAt(0) !== 'w'; });
    still.forEach(function (r) { problems.push(r.label + ' is still in the new plan.'); });
    if (typeof previous.planElements === 'number' && typeof next.planElements === 'number' && next.planElements > previous.planElements)
      problems.push('The new plan resolved MORE elements (' + next.planElements + ') than the one it narrows (' + previous.planElements + ').');
    if (!next.token) problems.push('The new rehearsal issued no confirmation_token.');
    return problems;
  }

  var api = {
    TOOLS: TOOLS, detectTool: detectTool, isRehearsal: isRehearsal, normalize: normalize,
    subsetArguments: subsetArguments, applyArguments: applyArguments, checkNarrowed: checkNarrowed
  };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.ImpactAdapter = api;
})(typeof self !== 'undefined' ? self : this);
