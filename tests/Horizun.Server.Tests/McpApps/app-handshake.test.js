// Runs each Horizun MCP App's SHIPPED page under plain node against a scripted host,
// and pins the three points of the MCP Apps 2026-01-26 lifecycle the apps must keep
// (ext-apps specification/2026-01-26/apps.mdx):
//   1. the first thing a View sends is the ui/initialize request, and nothing else
//      leaves it before the host answers;
//   2. the first thing it sends after that answer is ui/notifications/initialized
//      ("Host MUST NOT send any request or notification to the View before it
//      receives an initialized notification");
//   3. tool calls are enabled by McpUiInitializeResult.hostCapabilities.serverTools,
//      and by nothing else when hostCapabilities is present.
// (The CSP half - _meta.ui.csp - is resource metadata and is asserted in C#.)
//
// No browser, no packages: the inline <script> elements run in a vm context with a
// minimal fake DOM, and window.parent.postMessage is the host's inbox.
//
// Usage: node app-handshake.test.js <clash-viewer.html> <impact-preview.html> <impact fixture.json>
// Exit 0 when every check passes; prints one line per check.
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const [clashHtmlPath, impactHtmlPath, impactFixturePath] = process.argv.slice(2).map(p => path.resolve(p));
const clashHtml = fs.readFileSync(clashHtmlPath, 'utf8');
const impactHtml = fs.readFileSync(impactHtmlPath, 'utf8');
const impactFixture = JSON.parse(fs.readFileSync(impactFixturePath, 'utf8'));

let failed = 0, passed = 0;
function check(name, fn) {
  try { fn(); passed++; console.log('PASS ' + name); }
  catch (e) { failed++; console.log('FAIL ' + name + ' :: ' + (e && e.stack || e)); }
}

// ---- a DOM just large enough for the two views ------------------------------------
function makeDom() {
  const byId = {};
  let rowCache = { html: null, rows: [] };
  class El {
    constructor(tag) {
      this.tagName = String(tag || 'div').toUpperCase();
      this.children = []; this.attrs = {}; this.listeners = {};
      this._text = ''; this._html = ''; this.className = '';
      this.checked = false; this.disabled = false; this.title = '';
      this.style = { setProperty() {} };
      this.classList = { toggle() {}, add() {}, remove() {}, contains() { return false; } };
    }
    setAttribute(k, v) { this.attrs[k] = String(v); if (k === 'id') byId[v] = this; }
    getAttribute(k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; }
    appendChild(c) { this.children.push(c); return c; }
    removeChild(c) { const i = this.children.indexOf(c); if (i >= 0) this.children.splice(i, 1); return c; }
    get firstChild() { return this.children[0] || null; }
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
    dispatch(type) {
      const ev = { type, currentTarget: this, target: this, preventDefault() {} };
      (this.listeners[type] || []).forEach(fn => fn(ev));
    }
    set textContent(v) { this._text = String(v); this.children = []; }
    get textContent() { return this._text + this.children.map(c => c.textContent || '').join(''); }
    set innerHTML(v) { this._html = String(v); this.children = []; }
    get innerHTML() { return this._html; }
  }
  // The clash viewer renders its rows as markup; the fake reads them back from it.
  function rowsFromMarkup() {
    const html = Object.values(byId).map(e => e._html).join('');
    if (html === rowCache.html) return rowCache.rows;
    const rows = [];
    const re = /<tr class="row" data-index="(\d+)"/g;
    let m;
    while ((m = re.exec(html))) { const r = new El('tr'); r.attrs['data-index'] = m[1]; rows.push(r); }
    rowCache = { html, rows };
    return rows;
  }
  const document = {
    documentElement: new El('html'),
    getElementById(id) { if (!byId[id]) { const e = new El('div'); e.attrs.id = id; byId[id] = e; } return byId[id]; },
    createElement(tag) { return new El(tag); },
    createTextNode(text) { return { textContent: String(text) }; },
    querySelectorAll(sel) { return sel === 'tr.row' ? rowsFromMarkup() : []; }
  };
  return { document, byId };
}

// ---- load one page, with a host that only listens ----------------------------------
function load(html) {
  const { document, byId } = makeDom();
  const sent = [];
  const listeners = [];
  const context = {
    document, console,
    parent: { postMessage(message) { sent.push(JSON.parse(JSON.stringify(message))); } },
    addEventListener(type, fn) { if (type === 'message') listeners.push(fn); }
  };
  context.window = context; context.self = context;
  vm.createContext(context);
  const scripts = [];
  const re = /<script>([\s\S]*?)<\/script>/g;
  let m;
  while ((m = re.exec(html))) scripts.push(m[1]);
  assert.ok(scripts.length > 0, 'the page has no inline script');
  scripts.forEach((code, i) => vm.runInContext(code, context, { filename: 'inline-script-' + i + '.js' }));
  return {
    sent, byId, document,
    deliver(message) { listeners.forEach(fn => fn({ data: message, source: context.parent })); },
    toolCalls() { return sent.filter(x => x.method === 'tools/call'); }
  };
}

function initResult(hostCapabilities, extra) {
  return Object.assign({
    protocolVersion: '2026-01-26',
    hostCapabilities: hostCapabilities,
    hostInfo: { name: 'scripted-host', version: '1' },
    hostContext: { theme: 'light' }
  }, extra || {});
}

function answerInit(app, result) {
  const init = app.sent.find(x => x.method === 'ui/initialize');
  assert.ok(init, 'no ui/initialize was sent');
  const before = app.sent.length;
  app.deliver({ jsonrpc: '2.0', id: init.id, result });
  return app.sent.slice(before);
}

// ---- what "the user acts" means in each app -----------------------------------------
const clashPayload = {
  coverage: 'host and one link',
  clashes: [{ a: { element_id: 111, document: 'HOST.rvt' }, b: { element_id: 222, document: 'LINK.rvt', link_instance_id: 9 }, kind: 'intersection' }]
};

const apps = [
  {
    name: 'clash-viewer',
    html: clashHtml,
    feed(app) {
      app.deliver({ jsonrpc: '2.0', method: 'ui/notifications/tool-input', params: { arguments: {} } });
      app.deliver({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { content: [], structuredContent: clashPayload } });
    },
    act(app) {
      const rows = app.document.querySelectorAll('tr.row');
      assert.ok(rows.length === 1, 'the tool result did not render a row');
      rows[0].dispatch('click');
    }
  },
  {
    name: 'impact-preview',
    html: impactHtml,
    feed(app) {
      app.deliver({ jsonrpc: '2.0', method: 'ui/notifications/tool-input', params: { arguments: impactFixture.arguments } });
      app.deliver({ jsonrpc: '2.0', method: 'ui/notifications/tool-result', params: { content: [], structuredContent: impactFixture.result } });
    },
    act(app) {
      // disabled is the view's own gate; the click is sent regardless, so the gate
      // under test is the view's state, not the button's attribute.
      app.byId.apply.dispatch('click');
    }
  }
];

for (const spec of apps) {
  check(spec.name + ': the first message is the ui/initialize REQUEST, and nothing follows it unanswered', () => {
    const app = load(spec.html);
    assert.ok(app.sent.length >= 1, 'nothing was sent');
    assert.strictEqual(app.sent[0].method, 'ui/initialize');
    assert.ok(app.sent[0].id !== undefined, 'ui/initialize must be a request (it carries an id)');
    assert.strictEqual(app.sent[0].params.protocolVersion, '2026-01-26');
    assert.strictEqual(app.sent.length, 1, 'sent before the host answered: ' + JSON.stringify(app.sent.slice(1)));
  });

  check(spec.name + ': even a user action before the answer sends nothing', () => {
    const app = load(spec.html);
    spec.feed(app);           // a non-conforming host that pushes early
    try { spec.act(app); } catch (e) { /* no row / no plan yet is acceptable here */ }
    assert.deepStrictEqual(app.sent.map(x => x.method), ['ui/initialize']);
  });

  check(spec.name + ': ui/notifications/initialized is the FIRST thing sent after the answer', () => {
    const app = load(spec.html);
    const after = answerInit(app, initResult({ serverTools: {} }));
    assert.ok(after.length >= 1, 'nothing was sent after the answer');
    assert.strictEqual(after[0].method, 'ui/notifications/initialized');
    assert.strictEqual(after[0].id, undefined, 'initialized is a notification: no id');
    assert.strictEqual(app.sent.filter(x => x.method === 'ui/notifications/initialized').length, 1);
  });

  check(spec.name + ': hostCapabilities.serverTools grants tool calls', () => {
    const app = load(spec.html);
    answerInit(app, initResult({ serverTools: { listChanged: false } }));
    spec.feed(app);
    spec.act(app);
    assert.strictEqual(app.toolCalls().length, 1, 'expected one tools/call, sent: ' + JSON.stringify(app.sent.map(x => x.method)));
    const index = app.sent.findIndex(x => x.method === 'tools/call');
    const initialized = app.sent.findIndex(x => x.method === 'ui/notifications/initialized');
    assert.ok(initialized >= 0 && initialized < index, 'tools/call went out before initialized');
  });

  check(spec.name + ': capabilities.tools WITHOUT hostCapabilities.serverTools grants nothing', () => {
    const app = load(spec.html);
    answerInit(app, initResult({}, { capabilities: { tools: {}, toolCall: true } }));
    spec.feed(app);
    spec.act(app);
    assert.strictEqual(app.toolCalls().length, 0, 'a tool call was sent without serverTools');
  });

  check(spec.name + ': hostCapabilities without serverTools grants nothing', () => {
    const app = load(spec.html);
    answerInit(app, initResult({ openLinks: {}, serverResources: {}, logging: {} }));
    spec.feed(app);
    spec.act(app);
    assert.strictEqual(app.toolCalls().length, 0);
  });

  check(spec.name + ': a host request it does not implement is answered, not ignored', () => {
    const app = load(spec.html);
    answerInit(app, initResult({}));
    const before = app.sent.length;
    app.deliver({ jsonrpc: '2.0', id: 'h-1', method: 'ui/some-future-request', params: {} });
    app.deliver({ jsonrpc: '2.0', id: 'h-2', method: 'ui/resource-teardown', params: {} });
    const replies = app.sent.slice(before);
    const unknown = replies.find(x => x.id === 'h-1');
    assert.ok(unknown && unknown.error && unknown.error.code === -32601, 'no -32601 for an unknown request');
    const teardown = replies.find(x => x.id === 'h-2');
    assert.ok(teardown && teardown.result && !teardown.error, 'ui/resource-teardown was not answered');
  });

  check(spec.name + ': speaks only 2026-01-26 method names', () => {
    const app = load(spec.html);
    answerInit(app, initResult({ serverTools: {} }));
    spec.feed(app);
    spec.act(app);
    const known = ['ui/initialize', 'ui/notifications/initialized', 'tools/call', 'ui/update-model-context',
                   'ui/message', 'ui/open-link', 'ui/notifications/size-changed', 'notifications/message'];
    for (const x of app.sent) if (x.method) assert.ok(known.includes(x.method), 'not a spec method: ' + x.method);
  });
}

console.log((failed ? 'FAILED ' : 'OK ') + passed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);
