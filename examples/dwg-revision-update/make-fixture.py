# -*- coding: utf-8 -*-
"""Block 8, priorities 3 and 7: a synthetic drawing with successive revisions, and its frozen truth.

    python make_split_revisions.py <folder>

Nothing of any client is in here: every entity is written by entmake, the whole drawing is four networks
of straight ducts and their labels, and the file it produces is reproducible from this script alone.

The revisions, and what each one is FOR:

  R0  the network as first issued                     the model is built from this
  R1  run A is drawn as two pieces of different size  a division appears, and a transition with it
  R2  the division of A moves                         the same two pieces, cut somewhere else
  R3  A is one run again                              the division disappears; two pieces become one
  R4  the division of B moves                         a piece CONNECTED AT BOTH ENDS is re-cut
  R5  A as in R1                                      the copy the failure and recovery cases run on

R4 matters because b2 has an elbow at each end: whatever happens to it has to happen to its fittings
too, and a procedure that can only re-shape a free-ended duct will fail there and nowhere else.

The truth file is written BEFORE anything is run and is what the verifier scores against - it names,
per revision, which runs the drawing shows, how long each is and what size its label gives it.
"""
import io
import json
import os
import subprocess
import sys

ENGINE = os.environ.get('HZ_ACCORECONSOLE',
                        r'C:\Program Files\Autodesk\AutoCAD 2025ccoreconsole.exe')
IN = 25.4

# name -> (x0, y0, x1, y1, label)   inches; the label sits 8 in off the line's first half
R = {}
R['R0'] = [
    ('A',  0, 0, 400, 0, '12X8'),
    ('B1', 0, -300, 200, -300, '10X8'),
    ('B2', 200, -300, 200, -450, '10X8'),
    ('B3', 200, -450, 400, -450, '10X8'),
    ('C1', 0, -700, 200, -700, '8X6'),
    ('C2', 200, -700, 400, -700, '8X6'),
]
R['R1'] = [
    ('A1', 0, 0, 250, 0, '12X8'),
    ('A2', 250, 0, 400, 0, '10X8'),
] + R['R0'][1:]
R['R2'] = [
    ('A1', 0, 0, 300, 0, '12X8'),
    ('A2', 300, 0, 400, 0, '10X8'),
] + R['R0'][1:]
R['R3'] = list(R['R0'])
R['R4'] = R['R1'][:2] + [
    ('B1', 0, -300, 200, -300, '10X8'),
    ('B2', 200, -300, 200, -500, '10X8'),
    ('B3', 200, -500, 400, -500, '10X8'),
] + R['R0'][4:]
R['R5'] = list(R['R1'])

HEAD = '''(setvar "FILEDIA" 0)
(setvar "CMDDIA" 0)
(setvar "INSUNITS" 1)
(entmake (list (cons 0 "LAYER") (cons 100 "AcDbSymbolTableRecord") (cons 100 "AcDbLayerTableRecord") (cons 2 "M-SUPPLY") (cons 70 0) (cons 62 4)))
(entmake (list (cons 0 "LAYER") (cons 100 "AcDbSymbolTableRecord") (cons 100 "AcDbLayerTableRecord") (cons 2 "M-TEXT") (cons 70 0) (cons 62 2)))
'''


def label_at(x0, y0, x1, y1):
    """A quarter along the line and 8 in to its left, so it is nearer its own run than any other."""
    qx, qy = x0 + (x1 - x0) * 0.25, y0 + (y1 - y0) * 0.25
    vertical = abs(x1 - x0) < 1e-9
    return (qx + 8.0, qy, 1.5707963267948966) if vertical else (qx, qy + 8.0, 0.0)


def script_for(rows, out_dwg):
    s = HEAD
    for _, x0, y0, x1, y1, _ in rows:
        s += '(entmake (list (cons 0 "LINE") (cons 8 "M-SUPPLY") (list 10 %.1f %.1f 0.0) (list 11 %.1f %.1f 0.0)))\n' % (x0, y0, x1, y1)
    for _, x0, y0, x1, y1, lab in rows:
        lx, ly, rot = label_at(x0, y0, x1, y1)
        s += '(entmake (list (cons 0 "TEXT") (cons 8 "M-TEXT") (list 10 %.1f %.1f 0.0) (cons 40 4.0) (cons 1 "%s") (cons 50 %.10f)))\n' % (lx, ly, lab, rot)
    s += '(command "_.SAVEAS" "2018" "%s")\n' % out_dwg.replace('\\', '/')
    return s


def size_mm(label):
    w, h = label.lower().split('x')
    return [round(float(w) * IN, 1), round(float(h) * IN, 1)]


def truth():
    t = {'units': 'inch',
         'note': 'a SYNTHETIC drawing written by make_split_revisions.py; no project data is in it',
         'layers': {'ducts': 'M-SUPPLY', 'labels': 'M-TEXT'},
         'revisions': {}}
    for rev, rows in sorted(R.items()):
        t['revisions'][rev] = {
            'runs': [{'name': n, 'from_mm': [round(x0 * IN, 1), round(y0 * IN, 1)],
                      'to_mm': [round(x1 * IN, 1), round(y1 * IN, 1)],
                      'length_mm': round((abs(x1 - x0) + abs(y1 - y0)) * IN, 1),
                      'label': lab, 'size_mm': size_mm(lab)} for n, x0, y0, x1, y1, lab in rows],
            'run_count': len(rows)}
    t['expected'] = {
        'R0->R1': {'what': 'run A is now two pieces of different section',
                   'split_of': 'A', 'into': ['A1', 'A2'],
                   'kept_piece': 'A1 (the longest)', 'created': ['A2'], 'removed': [],
                   'a_transition_belongs_between_them': True,
                   'because': 'A1 is 12x8 and A2 is 10x8, and they meet end to end'},
        'R1->R2': {'what': 'the division moves from 250 to 300 in',
                   'kept_piece': 'A1, re-shaped', 'created': [], 'removed': [],
                   'moved_mm': round(50 * IN, 1)},
        'R2->R3': {'what': 'the division disappears',
                   'merge_of': ['A1', 'A2'], 'into': 'A',
                   'kept_piece': 'one of them, re-shaped to the whole line', 'removed': ['the other']},
        'R1->R4': {'what': 'the division of B moves, and B2 has an elbow at each end',
                   'affects_fittings': True,
                   'because': 'B2 is joined to B1 and B3 through fittings, so re-shaping it moves them too'},
        'R5': {'what': 'the same drawing as R1, for the failure and recovery cases'}
    }
    return t


def main(folder):
    base = os.path.abspath(folder)
    if os.path.exists(base):
        raise SystemExit('%s exists; it is never overwritten' % base)
    os.makedirs(base)
    made = {}
    for rev, rows in sorted(R.items()):
        d = os.path.join(base, rev)
        os.makedirs(d)
        dwg = os.path.join(d, 'MEP-SPLIT.dwg')
        scr = os.path.join(d, 'MEP-SPLIT.scr')
        io.open(scr, 'w', encoding='utf-8', newline='\r\n').write(script_for(rows, dwg))
        r = subprocess.run([ENGINE, '/s', scr, '/l', 'en-US'], capture_output=True, timeout=900)
        io.open(os.path.join(d, 'MEP-SPLIT.log'), 'wb').write(r.stdout + r.stderr)
        made[rev] = {'dwg': dwg, 'exists': os.path.exists(dwg),
                     'bytes': os.path.getsize(dwg) if os.path.exists(dwg) else 0, 'exit_code': r.returncode}
    t = truth()
    t['made'] = made
    io.open(os.path.join(base, 'MEP-SPLIT.truth.json'), 'w', encoding='utf-8').write(json.dumps(t, indent=1))
    print(json.dumps({k: {'exists': v['exists'], 'bytes': v['bytes']} for k, v in made.items()}, indent=1))
    ok = all(v['exists'] for v in made.values())
    print('all revisions written:', ok)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1]))
