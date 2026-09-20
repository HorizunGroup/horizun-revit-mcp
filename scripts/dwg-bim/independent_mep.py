# -*- coding: utf-8 -*-
"""Independent acceptance of a converted duct network: the DRAWING (an AutoCAD dump of the copy, read here
with its own simple reading) against the MODEL (ducts re-read from Revit). No product code decides what the
drawing says.

    python scripts/dwg-bim/independent_mep.py --tsv <dump.tsv> --document <title> --out <folder>
        [--duct-layer M-Main-Duct] [--label-layer M-TEXT] [--mm-per-unit 25.4]
        [--expect-ducts N | --expect-min-ducts N | --expect-empty] [--plan <plan reply json>]

A CHECK THAT LOOKED AT NOTHING IS NOT A PASS. Campaign 6 measured it: the server variable was missing,
the query failed, zero ducts came back and the step exited 0. Every one of these is now `accepted: false`
with its own non-zero exit code:

    2  no server (HORIZUN_SERVER_EXE unset or not a file)          -> nothing was asked
    3  the call failed (bridge refused, wrong active document, ...) -> nothing was read
    4  an empty population nobody declared                         -> nothing was compared
    5  incomplete evidence (a duct without connectors or size)     -> not everything was compared
    6  the expectation on the count is not met
    7  the frame does not agree (the ducts are not on the drawing's lines)

An empty population is valid only with --expect-empty. Every attempt is written as its own file
(attempt-<n>.json) and never overwritten; `selection.json` names the LAST accepted attempt and lists the
ones that were not, so a later good run is the evidence and the failed one stays beside it.
"""
import argparse
import json
import math
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SIZE = re.compile(r'^\s*(\d+(?:\.\d+)?)\s*[xX]\s*(\d+(?:\.\d+)?)\s*$')
EXIT = {'no_server': 2, 'call_failed': 3, 'empty_undeclared': 4, 'incomplete_evidence': 5,
        'count_mismatch': 6, 'frame_mismatch': 7}


class Refused(Exception):
    def __init__(self, reason, detail):
        Exception.__init__(self, '%s: %s' % (reason, detail))
        self.reason, self.detail = reason, detail


# --------------------------------------------------------------------------- the drawing, read here
def read_drawing(tsv, duct_layer, label_layer, mm):
    rows = [l.rstrip('\n').split('\t') for l in open(tsv, encoding='utf-8', errors='replace')]
    ms = [r for r in rows if len(r) > 5 and r[0] == 'E' and r[1] == '*Model_Space']
    segs, labels = [], []
    for r in ms:
        if r[3] in ('LWPOLYLINE', 'LINE') and r[4] == duct_layer:
            pts = [tuple(float(v) * mm for v in p.split(',')[:2]) for p in r[7].split(';') if p]
            for a, b in zip(pts, pts[1:]):
                if math.dist(a, b) > 1e-6:
                    segs.append({'entity': r[2], 'a': a, 'b': b})
        if r[3] in ('TEXT', 'MTEXT') and r[4] == label_layer:
            m = SIZE.match(r[-1])
            if m:
                labels.append({'entity': r[2], 'text': r[-1], 'at': (float(r[5]) * mm, float(r[6]) * mm),
                               'w': float(m.group(1)) * mm, 'h': float(m.group(2)) * mm})
    return segs, labels


def foot(p, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dy * dy
    t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2 if l2 else 0.0
    q = (a[0] + max(0.0, min(1.0, t)) * dx, a[1] + max(0.0, min(1.0, t)) * dy)
    return math.dist(p, q), t


# --------------------------------------------------------------------------- the model, re-read
def call(tool, args, server):
    if not server or not os.path.isfile(server):
        raise Refused('no_server', 'HORIZUN_SERVER_EXE is %r' % server)
    d = tempfile.mkdtemp(prefix='hz-indep-')
    ap, op = os.path.join(d, 'args.json'), os.path.join(d, 'out.json')
    json.dump(args, open(ap, 'w', encoding='utf-8'))
    env = dict(os.environ, HORIZUN_SERVER_EXE=server)
    subprocess.run(['pwsh', '-NoProfile', '-File', os.path.join(REPO, 'scripts', 'hz-call.ps1'), '-Tool', tool,
                    '-ArgumentsPath', ap, '-Json', op, '-Quiet'], env=env, capture_output=True, timeout=900)
    if not os.path.exists(op):
        raise Refused('call_failed', 'hz-call wrote no reply')
    reply = json.load(open(op, encoding='utf-8-sig'))
    if reply.get('is_error') or not reply.get('replied', True):
        raise Refused('call_failed', (reply.get('raw') or 'error without text')[:400])
    res = reply.get('result') or {}
    return res.get('structuredContent', res) if isinstance(res, dict) else {}


def model_ducts(document, server, caller=call):
    sc = caller('horizun_query_model', {'categories': ['OST_DuctCurves'], 'target_document': document, 'max_rows': 2000,
                                        'include_mep': True, 'coordinate_units': 'mm',
                                        'return_parameters': ['RBS_CURVE_WIDTH_PARAM', 'RBS_CURVE_HEIGHT_PARAM',
                                                              'RBS_CURVE_DIAMETER_PARAM', 'RBS_OFFSET_PARAM']}, server)
    if sc.get('truncated'):
        raise Refused('incomplete_evidence', 'the query was truncated')
    return sc.get('rows') or sc.get('elements') or []


# --------------------------------------------------------------------------- judgement (pure, tested)
def judge(items, segs, labels, expect, beside_mm=600.0, frame_mm=25.4):
    """Returns the report; raises Refused for every way the comparison would be empty or partial."""
    if not items:
        if expect.get('empty'):
            return {'accepted': True, 'model_ducts': 0, 'rows': [], 'summary': {'declared_empty': True}}
        raise Refused('empty_undeclared', 'the model returned no duct and the expectation does not declare none')
    if not segs:
        raise Refused('incomplete_evidence', 'the drawing dump has no segment on the duct layer')
    rows, missing = [], []
    for it in items:
        mep = it.get('mep') or {}
        cons = mep.get('connectors') or []
        params = it.get('parameters') or {}

        def pv(k):
            v = (params.get(k) or {}).get('raw')
            return v * 304.8 if isinstance(v, (int, float)) else None
        w, h, dia = pv('RBS_CURVE_WIDTH_PARAM'), pv('RBS_CURVE_HEIGHT_PARAM'), pv('RBS_CURVE_DIAMETER_PARAM')
        if len(cons) < 2 or not ((w and h) or dia):
            missing.append(it.get('element_id'))
            continue
        s, e = cons[0]['origin'], cons[1]['origin']
        mid = ((s[0] + e[0]) / 2, (s[1] + e[1]) / 2)
        seg = min(segs, key=lambda g: foot(mid, g['a'], g['b'])[0])
        off = foot(mid, seg['a'], seg['b'])[0]
        near = [l for l in labels if foot(l['at'], seg['a'], seg['b'])[0] <= beside_mm
                and -0.02 <= foot(l['at'], seg['a'], seg['b'])[1] <= 1.02]
        row = {'id': it.get('element_id'), 'mid': [round(mid[0], 1), round(mid[1], 1)], 'width': w, 'height': h, 'diameter': dia, 'offset_mm': pv('RBS_OFFSET_PARAM'),
               'drawn_entity': seg['entity'], 'drawn_segment_offset_mm': round(off, 1),
               'labels_beside_segment': [{'entity': l['entity'], 'text': l['text']} for l in near]}
        if near and w and h:
            row['label_agrees'] = any(abs(l['w'] - w) < 1 and abs(l['h'] - h) < 1 for l in near)
        rows.append(row)
    if missing:
        raise Refused('incomplete_evidence', 'ducts without two connectors or a size: %s' % missing)
    bad_frame = [r['id'] for r in rows if r['drawn_segment_offset_mm'] > frame_mm]
    if bad_frame:
        raise Refused('frame_mismatch', 'ducts off every drawn line by more than %.1f mm: %s' % (frame_mm, bad_frame))
    n = len(rows)
    if expect.get('count') is not None and n != expect['count']:
        raise Refused('count_mismatch', 'expected %d ducts, the model has %d' % (expect['count'], n))
    if expect.get('min') is not None and n < expect['min']:
        raise Refused('count_mismatch', 'expected at least %d ducts, the model has %d' % (expect['min'], n))
    summary = {'label_beside_and_agrees': sum(1 for r in rows if r.get('label_agrees') is True),
               'label_beside_and_disagrees': sum(1 for r in rows if r.get('label_agrees') is False),
               'no_label_beside_its_segment': sum(1 for r in rows if 'label_agrees' not in r),
               'means': 'a label within %d mm of the drawn segment\'s interior - which is NOT the product\'s '
                        '"documented": one label beside a corner lies beside both legs here, while the product '
                        'gives it to one run. See per_run_vs_product when a plan is supplied.' % beside_mm}
    return {'accepted': True, 'model_ducts': n, 'rows': rows, 'summary': summary}


def versus_product(rows, plan):
    """Per duct: what the product's section reading said, next to what this check sees. Explains, never adjusts."""
    sec = (plan.get('sections') or {}).get('by_rule') or {}
    runs = [x for rule in sec.values() for x in (rule.get('rows') or [])]
    out = []
    for r in rows:
        # a duct belongs to the product run whose line carries its midpoint (the ids differ by design:
        # the product names runs by revision id, the dump by handle)
        best, bd = None, None
        for x in runs:
            a, b = x.get('from_mm'), x.get('to_mm')
            if not a or not b:
                continue
            d, t = foot(r['mid'], a, b)
            if 0.0 <= t <= 1.0 and d <= 25.4 and (bd is None or d < bd):
                best, bd = x, d
        if best is None:
            out.append({'id': r['id'], 'product': None, 'why': 'no product run carries this duct'})
            continue
        product_labels = [l.get('text') for l in best.get('labels') or []]
        mine = [l['text'] for l in r['labels_beside_segment']]
        if best['state'] == 'documented' and mine:
            why = 'both see a label'
        elif best['state'] == 'propagated' and mine:
            why = ('a label lies beside this segment but the product gave it to another run (nearest run / its '
                   'leader), and sized this one by propagation')
        elif best['state'] == 'propagated':
            why = 'no label beside; sized by propagation'
        else:
            why = 'product state %s' % best['state']
        out.append({'id': r['id'], 'product_state': best['state'], 'product_labels': product_labels,
                    'labels_beside_here': mine, 'propagated_from': best.get('propagated_from'), 'why': why})
    return out


# --------------------------------------------------------------------------- attempts and selection
def record(folder, report):
    os.makedirs(folder, exist_ok=True)
    n = 1
    while os.path.exists(os.path.join(folder, 'attempt-%d.json' % n)):
        n += 1
    path = os.path.join(folder, 'attempt-%d.json' % n)
    json.dump(report, open(path, 'w', encoding='utf-8'), indent=1)
    sel_path = os.path.join(folder, 'selection.json')
    sel = json.load(open(sel_path, encoding='utf-8')) if os.path.exists(sel_path) else {'not_accepted': []}
    if report['accepted']:
        sel['selected'] = os.path.basename(path)
    else:
        sel['not_accepted'].append({'attempt': os.path.basename(path), 'reason': report.get('reason')})
    sel['means'] = 'selected is the LAST accepted attempt; every attempt that was not accepted stays listed and on disk'
    json.dump(sel, open(sel_path, 'w', encoding='utf-8'), indent=1)
    return path


def main(argv=None, caller=call):
    ap = argparse.ArgumentParser()
    ap.add_argument('--tsv', required=True)
    ap.add_argument('--document', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--duct-layer', default='M-Main-Duct')
    ap.add_argument('--label-layer', default='M-TEXT')
    ap.add_argument('--mm-per-unit', type=float, default=25.4)
    g = ap.add_mutually_exclusive_group()
    g.add_argument('--expect-ducts', type=int)
    g.add_argument('--expect-min-ducts', type=int)
    g.add_argument('--expect-empty', action='store_true')
    ap.add_argument('--plan')
    a = ap.parse_args(argv)
    expect = {'count': a.expect_ducts, 'min': a.expect_min_ducts, 'empty': a.expect_empty}
    base = {'document': a.document, 'tsv': a.tsv, 'expectation': expect}
    try:
        segs, labels = read_drawing(a.tsv, a.duct_layer, a.label_layer, a.mm_per_unit)
        base.update(drawing_segments=len(segs), drawing_size_labels=len(labels))
        items = model_ducts(a.document, os.environ.get('HORIZUN_SERVER_EXE'), caller)
        report = dict(base, **judge(items, segs, labels, expect))
        if a.plan:
            plan = json.load(open(a.plan, encoding='utf-8-sig'))
            plan = (plan.get('result') or {}).get('structuredContent', plan) if 'result' in plan else plan
            report['per_run_vs_product'] = versus_product(report['rows'], plan)
        code = 0
    except Refused as r:
        report = dict(base, accepted=False, reason=r.reason, detail=r.detail)
        code = EXIT[r.reason]
    path = record(a.out, report)
    print(json.dumps({'accepted': report['accepted'], 'reason': report.get('reason'), 'attempt': path,
                      'summary': report.get('summary')}))
    return code


if __name__ == '__main__':
    sys.exit(main())
