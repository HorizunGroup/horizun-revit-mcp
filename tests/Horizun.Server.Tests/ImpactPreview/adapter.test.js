// Runs the impact-preview adapter under plain node - no browser, no packages.
// Usage: node adapter.test.js <path to impact-preview-adapter.js> <fixtures dir>
// Exit 0 when every check passes; prints one line per check.
'use strict';
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const adapterPath = process.argv[2];
const fixturesDir = process.argv[3] || path.join(__dirname, 'fixtures');
const A = require(path.resolve(adapterPath));
const load = (n) => JSON.parse(fs.readFileSync(path.join(fixturesDir, n), 'utf8'));

let failed = 0, passed = 0;
function check(name, fn) {
  try { fn(); passed++; console.log('PASS ' + name); }
  catch (e) { failed++; console.log('FAIL ' + name + ' :: ' + (e && e.message)); }
}

const fixtures = ['write-params.json', 'set-keynote.json', 'delete-ids.json', 'transform.json', 'create.json'].map(load);

for (const f of fixtures) {
  check(f.tool + ': detected from the payload shape alone', () => assert.strictEqual(A.detectTool(f.result, null), f.tool));
  check(f.tool + ': a rehearsal with a token, and every row excludable', () => {
    const m = A.normalize(f.result, f.tool, f.arguments);
    assert.ok(m.rehearsal); assert.strictEqual(m.token, f.result.confirmation_token);
    assert.ok(m.rows.length > 0);
    const requested = m.rows.filter(r => !/cascade/.test(r.note));
    assert.ok(requested.every(r => r.excludable), 'not excludable: ' + requested.filter(r => !r.excludable).map(r => r.whyNot).join('; '));
  });
  check(f.tool + ': apply spends THIS rehearsal\'s token with its own arguments', () => {
    const m = A.normalize(f.result, f.tool, f.arguments);
    const ap = A.applyArguments(m, f.arguments, 'key-1');
    assert.ok(ap.ok, ap.reason);
    assert.strictEqual(ap.arguments.dry_run, false);
    assert.strictEqual(ap.arguments.confirmation_token, f.result.confirmation_token);
    assert.strictEqual(ap.arguments.idempotency_key, 'key-1');
    const { dry_run, confirmation_token, idempotency_key, ...scope } = ap.arguments;
    assert.deepStrictEqual(scope, f.arguments);
  });
  check(f.tool + ': excluding a row yields a NEW rehearsal, never an apply', () => {
    const m = A.normalize(f.result, f.tool, f.arguments);
    const first = m.rows.find(r => r.excludable);
    const withToken = Object.assign({}, f.arguments, { confirmation_token: 'old', idempotency_key: 'old-key', dry_run: false });
    const sub = A.subsetArguments(m, withToken, [first.key]);
    assert.ok(sub.ok, sub.reason);
    assert.strictEqual(sub.arguments.dry_run, true);
    assert.ok(!('confirmation_token' in sub.arguments));
    assert.ok(!('idempotency_key' in sub.arguments));
    assert.ok(sub.removed >= 1);
    assert.strictEqual(sub.arguments.target_document, f.arguments.target_document);
  });
  check(f.tool + ': the caller\'s arguments are not mutated', () => {
    const before = JSON.stringify(f.arguments);
    const m = A.normalize(f.result, f.tool, f.arguments);
    A.subsetArguments(m, f.arguments, [m.rows[0].key]);
    assert.strictEqual(JSON.stringify(f.arguments), before);
  });
  check(f.tool + ': without the original arguments nothing can be sent', () => {
    const m = A.normalize(f.result, f.tool, null);
    assert.ok(m.rows.every(r => !r.excludable));
    assert.ok(!A.applyArguments(m, null, 'k').ok);
    assert.ok(!A.subsetArguments(m, null, [m.rows[0].key]).ok);
  });
}

const [wp, kn, del, tr, cr] = fixtures;

check('write_params: before -> after is read from the rows, JSON strings unquoted', () => {
  const m = A.normalize(wp.result, wp.tool, wp.arguments);
  assert.deepStrictEqual(m.rows[1].changes[0], { name: 'Comments', before: 'old', after: 'B' });
  assert.strictEqual(m.rows[0].category, 'Walls');
});
check('write_params: excluding write #1 drops exactly writes[1]', () => {
  const m = A.normalize(wp.result, wp.tool, wp.arguments);
  const sub = A.subsetArguments(m, wp.arguments, ['w:1']);
  assert.deepStrictEqual(sub.arguments.writes.map(w => w.target_id), [1001, 2001]);
});
check('write_params: a CSV-expanded rehearsal cannot be narrowed row by row', () => {
  const args = { target_document: 'HZ_WRITE', tabular_source: { path: 'x.csv' } };
  const m = A.normalize(wp.result, wp.tool, args);
  assert.ok(m.rows.every(r => !r.excludable));
  assert.ok(!A.subsetArguments(m, args, ['w:0']).ok);
});
check('write_params: warnings name the collateral and the cascade', () => {
  const m = A.normalize(wp.result, wp.tool, wp.arguments);
  assert.ok(m.warnings.some(w => /did not name/.test(w)));
  assert.ok(m.warnings.some(w => /consequence/.test(w)));
});
check('set_keynote: excluding a TYPE target removes every id that resolved to it', () => {
  const m = A.normalize(kn.result, kn.tool, kn.arguments);
  const sub = A.subsetArguments(m, kn.arguments, ['t:2001']);
  assert.deepStrictEqual(sub.arguments.element_ids, [3001]);
  assert.deepStrictEqual(m.rows[1].changes[0], { name: 'Keynote', before: '08 14 00', after: 'K-01' });
  assert.strictEqual(m.rows[1].category, 'Doors');
});
check('delete ids: a cascade row cannot be excluded on its own', () => {
  const m = A.normalize(del.result, del.tool, del.arguments);
  const cascade = m.rows.find(r => r.key === 'd:5003');
  assert.ok(!cascade.excludable); assert.ok(/5002/.test(cascade.whyNot));
  assert.ok(!A.subsetArguments(m, del.arguments, ['d:5003']).ok);
});
check('delete ids: excluding an id removes it from ids', () => {
  const m = A.normalize(del.result, del.tool, del.arguments);
  assert.deepStrictEqual(A.subsetArguments(m, del.arguments, ['d:5002']).arguments.ids, [5001]);
});
check('delete purge_unused: excluding an id PROTECTS it instead', () => {
  const args = { target_document: 'HZ_WRITE', mode: 'purge_unused', protect_ids: [9] };
  const payload = JSON.parse(JSON.stringify(del.result)); payload.mode = 'purge_unused';
  const m = A.normalize(payload, del.tool, args);
  const sub = A.subsetArguments(m, args, ['d:5001']);
  assert.deepStrictEqual(sub.arguments.protect_ids, [9, 5001]);
});
check('delete: excluding every id is refused rather than sent', () => {
  const m = A.normalize(del.result, del.tool, del.arguments);
  assert.ok(!A.subsetArguments(m, del.arguments, ['d:5001', 'd:5002']).ok);
});
check('transform: an operation left with no ids is dropped, the others keep order', () => {
  const m = A.normalize(tr.result, tr.tool, tr.arguments);
  const sub = A.subsetArguments(m, tr.arguments, ['e:6003', 'e:6001']);
  assert.deepStrictEqual(sub.arguments.operations, [{ operation: 'move', element_ids: [6002], vector: [100, 0, 0] }]);
  assert.deepStrictEqual(m.rows[2].changes[1], { name: 'type_id', before: '7000', after: '7001' });
});
check('create: summary by category and by level; excluding #2 drops elements[1]', () => {
  const m = A.normalize(cr.result, cr.tool, cr.arguments);
  assert.deepStrictEqual(m.byCategory, [{ name: 'wall', count: 2 }, { name: 'pipe', count: 1 }]);
  assert.deepStrictEqual(m.byLevel, [{ name: 'Level 1', count: 2 }, { name: 'Level 2', count: 1 }]);
  const sub = A.subsetArguments(m, cr.arguments, ['c:1']);
  assert.deepStrictEqual(sub.arguments.elements.map(e => e.level_id), [311, 311]);
});
check('a truncated preview says the hidden rows WILL be applied', () => {
  const p = JSON.parse(JSON.stringify(tr.result)); p.change_preview.truncated = true; p.change_preview.total = 120;
  const m = A.normalize(p, tr.tool, tr.arguments);
  assert.ok(m.truncated); assert.strictEqual(m.total, 120);
  assert.ok(m.warnings.some(w => /WILL be applied/.test(w)));
});
check('no token (withheld) -> no apply', () => {
  const p = JSON.parse(JSON.stringify(del.result)); delete p.confirmation_token; p.confirmation_withheld = true;
  const m = A.normalize(p, del.tool, del.arguments);
  assert.ok(!A.applyArguments(m, del.arguments, 'k').ok);
  assert.ok(m.warnings.some(w => /withheld/.test(w)));
});
check('an APPLY result is not a rehearsal and cannot be re-applied', () => {
  const p = { mode: 'atomic', transaction_status: 'Committed', writes_planned: 3, on_failure_if_run: 'atomic',
              application: { state: 'verified_applied' }, rows: [] };
  const m = A.normalize(p, wp.tool, wp.arguments);
  assert.ok(!m.rehearsal); assert.ok(!A.applyArguments(m, wp.arguments, 'k').ok);
});
check('checkNarrowed flags a plan that grew or kept an excluded element', () => {
  const before = A.normalize(tr.result, tr.tool, tr.arguments);
  const grown = JSON.parse(JSON.stringify(tr.result)); grown.plan_resolved.elements = 9;
  const after = A.normalize(grown, tr.tool, tr.arguments);
  const problems = A.checkNarrowed(before, after, ['e:6001']);
  assert.ok(problems.some(p => /still in the new plan/.test(p)));
  assert.ok(problems.some(p => /MORE elements/.test(p)));
});
check('an unknown payload renders nothing excludable', () => {
  const m = A.normalize({ something: 1 }, null, {});
  assert.ok(!m.known); assert.strictEqual(m.rows.length, 0);
});

console.log((failed ? 'FAILED ' : 'OK ') + passed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);
