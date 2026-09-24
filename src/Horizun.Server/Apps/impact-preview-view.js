/*
 * Horizun impact preview - the view. Original Horizun code.
 *
 * Renders ONLY what the rehearsal already said; fetches nothing. The one thing it can
 * do beyond display is ask the host to call the SAME tool again - either a narrower
 * REHEARSAL (when rows are excluded) or the apply of the rehearsal on screen with that
 * rehearsal's own token. Nothing that was not rehearsed is ever applied from here.
 */
(function () {
  'use strict';
  var A = window.ImpactAdapter;
  var state = {
    canCall: false, toolName: null, args: null, rehearsedArgs: null, payload: null, model: null,
    excluded: {}, phase: 'waiting', pending: {}, applyKey: null, applyToken: null, lastExcluded: []
  };
  var nextId = 1;

  function $(id) { return document.getElementById(id); }
  function el(tag, attrs, text) {
    var n = document.createElement(tag);
    if (attrs) for (var k in attrs) if (Object.prototype.hasOwnProperty.call(attrs, k)) n.setAttribute(k, attrs[k]);
    if (text !== undefined && text !== null) n.textContent = String(text);
    return n;
  }
  function clear(n) { while (n.firstChild) n.removeChild(n.firstChild); }

  function post(method, params, what) {
    var id = nextId++;
    if (what) state.pending[id] = what;
    window.parent.postMessage({ jsonrpc: '2.0', id: id, method: method, params: params || {} }, '*');
    return id;
  }
  function notify(method, params) { window.parent.postMessage({ jsonrpc: '2.0', method: method, params: params || {} }, '*'); }
  function reply(id, result) { window.parent.postMessage({ jsonrpc: '2.0', id: id, result: result || {} }, '*'); }

  function uuid() {
    var c = window.crypto || null;
    if (c && typeof c.randomUUID === 'function') { try { return c.randomUUID(); } catch (e) { /* fall through */ } }
    var b = new Uint8Array(16);
    if (c && c.getRandomValues) c.getRandomValues(b); else for (var i = 0; i < 16; i++) b[i] = Math.floor(Math.random() * 256);
    b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128;
    var h = Array.prototype.map.call(b, function (x) { return (x + 256).toString(16).substring(1); }).join('');
    return h.substr(0, 8) + '-' + h.substr(8, 4) + '-' + h.substr(12, 4) + '-' + h.substr(16, 4) + '-' + h.substr(20);
  }

  function setStatus(kind, text) { var s = $('status'); s.setAttribute('data-kind', kind || ''); s.textContent = text || ''; }

  function applyTheme(ctx) {
    if (!ctx) return;
    if (ctx.theme === 'light' || ctx.theme === 'dark') document.documentElement.setAttribute('data-theme', ctx.theme);
    var vars = ctx.styles && ctx.styles.variables;
    if (vars) for (var k in vars) if (/^--[A-Za-z0-9-]+$/.test(k) && typeof vars[k] === 'string') document.documentElement.style.setProperty(k, vars[k]);
    if (ctx.toolInfo && ctx.toolInfo.tool && ctx.toolInfo.tool.name) state.toolName = ctx.toolInfo.tool.name;
  }

  function excludedKeys() { return Object.keys(state.excluded).filter(function (k) { return state.excluded[k]; }); }

  function renderSummary(m) {
    var box = $('summary'); clear(box);
    var c1 = el('div', { 'class': 'card' });
    c1.appendChild(el('h2', null, m.label));
    c1.appendChild(el('div', { 'class': 'big' }, typeof m.planElements === 'number' ? m.planElements : m.total));
    c1.appendChild(el('div', { 'class': 'note' }, 'element(s) in the resolved plan' + (m.truncated ? ' - ' + m.shown + ' shown' : '')));
    box.appendChild(c1);
    [['By category', m.byCategory], ['By level', m.byLevel]].forEach(function (pair) {
      if (!pair[1].length) return;
      var card = el('div', { 'class': 'card' });
      card.appendChild(el('h2', null, pair[0] + (m.truncated ? ' (shown rows)' : '')));
      var ul = el('ul');
      pair[1].slice(0, 8).forEach(function (x) { ul.appendChild(el('li', null, x.name + ': ' + x.count)); });
      if (pair[1].length > 8) ul.appendChild(el('li', null, '+' + (pair[1].length - 8) + ' more'));
      card.appendChild(ul); box.appendChild(card);
    });
  }

  function renderTable(m) {
    var wrap = $('table'); clear(wrap);
    if (!m.rows.length) { wrap.appendChild(el('p', { 'class': 'note', style: 'padding:0 12px' }, 'The rehearsal lists no rows.')); return; }
    var t = el('table');
    t.appendChild(el('caption', null, m.shown + ' row(s). Tick a row to EXCLUDE it; excluding asks for a new rehearsal first.'));
    var head = el('tr');
    var showLevel = m.rows.some(function (r) { return r.level; });
    ['Exclude', 'Element', 'Category', showLevel ? 'Level' : null, 'Change (before -> after)', 'Note'].forEach(function (h) {
      if (h) head.appendChild(el('th', { scope: 'col' }, h));
    });
    var thead = el('thead'); thead.appendChild(head); t.appendChild(thead);
    var tbody = el('tbody');
    m.rows.forEach(function (r) {
      var tr = el('tr', r.key && state.excluded[r.key] ? { 'class': 'excluded' } : null);
      var td0 = el('td');
      var box = el('input', { type: 'checkbox', 'aria-label': 'Exclude ' + r.label });
      box.checked = !!state.excluded[r.key];
      box.disabled = !r.excludable || state.phase !== 'preview';
      if (!r.excludable) box.title = r.whyNot;
      box.addEventListener('change', function () { state.excluded[r.key] = box.checked; render(); });
      td0.appendChild(box); tr.appendChild(td0);
      tr.appendChild(el('td', null, r.label));
      tr.appendChild(el('td', null, r.category));
      if (showLevel) tr.appendChild(el('td', null, r.level));
      var tdc = el('td');
      r.changes.forEach(function (c, i) {
        if (i) tdc.appendChild(el('br'));
        if (c.name) tdc.appendChild(el('span', { 'class': 'tag' }, c.name));
        tdc.appendChild(document.createTextNode(' ' + (c.before === '' ? '' : c.before)));
        tdc.appendChild(el('span', { 'class': 'arrow', 'aria-label': 'becomes' }, '->'));
        tdc.appendChild(document.createTextNode(c.after));
      });
      tr.appendChild(tdc);
      tr.appendChild(el('td', { 'class': 'note' }, r.note || (r.excludable ? '' : r.whyNot)));
      tbody.appendChild(tr);
    });
    t.appendChild(tbody);
    wrap.appendChild(t);
  }

  function renderActions(m) {
    var apply = $('apply'), clr = $('clear'), hint = $('hint');
    var n = excludedKeys().length;
    clr.disabled = n === 0 || state.phase !== 'preview';
    if (state.phase === 'applied') { apply.disabled = true; apply.textContent = 'Applied'; hint.textContent = 'This token has been spent. Rehearse again to make another change.'; return; }
    if (!m || !m.rehearsal) { apply.disabled = true; apply.textContent = 'Apply'; hint.textContent = m ? 'This result is not a rehearsal; there is nothing to apply.' : ''; return; }
    var busy = state.phase === 'narrowing' || state.phase === 'applying';
    if (n > 0) {
      apply.textContent = 'Rehearse without ' + n + ' excluded';
      apply.disabled = busy || !state.canCall;
      hint.textContent = 'A token approves ONE plan. Excluding rows asks for a NEW rehearsal; you apply after reviewing it.';
    } else {
      apply.textContent = 'Apply ' + (typeof m.planElements === 'number' ? m.planElements + ' ' : '') + 'as rehearsed';
      var ready = A.applyArguments(m, state.rehearsedArgs, 'probe');
      apply.disabled = busy || !state.canCall || !ready.ok;
      hint.textContent = !state.canCall ? 'This host has not granted tool calls: ask in the chat to apply this rehearsal with its confirmation_token.'
                       : (ready.ok ? 'Applies exactly the rehearsal shown, with its own confirmation_token' + (m.expires ? ' (expires ' + m.expires + ')' : '') + '.' : ready.reason);
    }
  }

  function render() {
    var m = state.model;
    if (!m) { renderActions(null); return; }
    $('title').textContent = m.label + (m.rehearsal ? ' - rehearsal' : '');
    $('subtitle').textContent = (m.document ? m.document + ' - ' : '') + (m.state ? 'state: ' + m.state : '') +
                                (m.stateMeans ? ' (' + m.stateMeans + ')' : '');
    renderSummary(m);
    var w = $('warnings'); clear(w);
    m.warnings.forEach(function (x) { w.appendChild(el('li', null, x)); });
    renderTable(m);
    renderActions(m);
  }

  function show(payload, args, afterNarrowing) {
    var previous = state.model;
    state.payload = payload;
    state.rehearsedArgs = args || null;
    state.model = A.normalize(payload, state.toolName, args);
    if (afterNarrowing && previous) {
      var problems = A.checkNarrowed(previous, state.model, state.lastExcluded);
      if (problems.length) setStatus('warn', 'Check the new rehearsal before applying: ' + problems.join(' '));
      else setStatus('done', 'New rehearsal ready without the excluded rows. Review it, then Apply.');
    }
    state.excluded = {};
    if (state.phase !== 'applied') state.phase = state.model.rehearsal ? 'preview' : 'result';
    render();
  }

  function textOf(result) {
    var parts = (result && result.content || []).filter(function (c) { return c && c.type === 'text'; }).map(function (c) { return c.text; });
    return parts.join(' ').substring(0, 600);
  }

  function onApply() {
    var m = state.model;
    if (!m || !state.canCall || state.phase !== 'preview') return;
    var keys = excludedKeys();
    if (keys.length) {
      var sub = A.subsetArguments(m, state.rehearsedArgs, keys);
      if (!sub.ok) { setStatus('failed', sub.reason); return; }
      state.lastExcluded = keys; state.phase = 'narrowing';
      setStatus('', 'Rehearsing again without ' + sub.removed + ' excluded entr' + (sub.removed === 1 ? 'y' : 'ies') + '...');
      post('tools/call', { name: m.tool, arguments: sub.arguments }, { kind: 'narrow', args: sub.arguments });
    } else {
      // One key per token: a retry of this same apply replays instead of writing twice.
      if (state.applyToken !== m.token) { state.applyToken = m.token; state.applyKey = uuid(); }
      var ap = A.applyArguments(m, state.rehearsedArgs, state.applyKey);
      if (!ap.ok) { setStatus('failed', ap.reason); return; }
      state.phase = 'applying';
      setStatus('', 'Applying the rehearsed plan...');
      post('tools/call', { name: m.tool, arguments: ap.arguments }, { kind: 'apply' });
    }
    render();
  }

  function onToolReply(what, message) {
    var result = message.result || {};
    var failed = !!message.error || result.isError === true;
    var payload = result.structuredContent || null;
    if (what.kind === 'narrow') {
      if (failed || !payload) {
        state.phase = 'preview';
        setStatus('failed', 'The new rehearsal failed; nothing was written. ' + (message.error ? message.error.message : textOf(result)));
        render(); return;
      }
      show(payload, what.args, true);
      post('ui/update-model-context', { content: [{ type: 'text', text: 'The impact preview re-rehearsed ' + state.model.tool +
        ' without ' + state.lastExcluded.length + ' excluded row(s); new plan: ' + state.model.planElements + ' element(s). Nothing was applied.' }] }, { kind: 'context' });
      return;
    }
    if (what.kind === 'apply') {
      var st = payload && payload.application ? payload.application.state : null;
      if (failed) {
        state.phase = 'preview';
        setStatus('failed', 'The apply was refused or failed: ' + (message.error ? message.error.message : textOf(result)));
      } else {
        state.phase = 'applied';
        setStatus(st === 'verified_applied' ? 'done' : 'warn', 'Apply returned state ' + (st || 'unknown') +
          (payload && payload.application && payload.application.state_means ? ': ' + payload.application.state_means : '.'));
      }
      post('ui/update-model-context', { content: [{ type: 'text', text: 'The impact preview applied ' + (state.model && state.model.tool) +
        ' with the rehearsed token; reply state: ' + (failed ? 'error' : (st || 'unknown')) + '.' }] }, { kind: 'context' });
      render();
    }
  }

  window.addEventListener('message', function (event) {
    var msg = event.data;
    if (!msg || typeof msg !== 'object' || msg.jsonrpc !== '2.0') return;
    if (msg.id !== undefined && !msg.method) {
      var what = state.pending[msg.id];
      if (!what) return;
      delete state.pending[msg.id];
      if (what.kind === 'init') {
        var r = msg.result || {};
        var caps = r.hostCapabilities || {};
        state.canCall = !!caps.serverTools;
        applyTheme(r.hostContext);
        notify('ui/notifications/initialized', {});
        render();
        return;
      }
      if (what.kind === 'context') return; // the host took (or refused) the context update; nothing to show
      onToolReply(what, msg);
      return;
    }
    var p = msg.params || {};
    switch (msg.method) {
      case 'ui/notifications/tool-input':
        state.args = p.arguments || null;
        if (state.payload && !state.rehearsedArgs) show(state.payload, state.args, false);
        break;
      case 'ui/notifications/tool-result':
        if (p.structuredContent) show(p.structuredContent, state.args, false);
        else { $('subtitle').textContent = 'The result carried no structured content to preview. ' + textOf(p); }
        break;
      case 'ui/notifications/tool-cancelled':
        setStatus('warn', 'The tool call was cancelled; there is nothing to apply.');
        break;
      case 'ui/notifications/host-context-changed':
        applyTheme(p); render();
        break;
      case 'ui/resource-teardown':
        if (msg.id !== undefined) reply(msg.id, {});
        break;
      default:
        if (msg.id !== undefined) window.parent.postMessage({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'Method not found' } }, '*');
    }
  });

  $('apply').addEventListener('click', onApply);
  $('clear').addEventListener('click', function () { state.excluded = {}; render(); });
  post('ui/initialize', {
    protocolVersion: '2026-01-26',
    appInfo: { name: 'horizun-impact-preview', version: '1' },
    appCapabilities: {}
  }, { kind: 'init' });
  render();
})();
