# -*- coding: utf-8 -*-
"""A SYNTHETIC rectangular-duct drawing with SIZE LABELS - generated, never copied from a project.

    python make_mep_rect_dwg.py <out_folder> [--engine <accoreconsole.exe>] [--template <acad.dwt>]

Writes <out_folder>/MEP-RECT.dwg and MEP-RECT.truth.json. Refuses to overwrite. Inches (INSUNITS 1),
single-line centrelines on M-SUPPLY / M-EXHAUST, sizes as TEXT on M-TEXT, one LEADER:

    A   (0,0)-(200,0)          "16X8" beside it                 documented 16x8
    C   (200,0)-(400,0)        "12X8" beside it                 documented 12x8  (A-C through a TEE at 200,0)
    BR  (200,0)-(200,-150)     "8X6" at (240,-100), LEADER from (200,-75) to the text: documented by leader
    L1  (400,0)-(400,200)      no label                         propagated 12x8 through the elbow at (400,0)
    D   (400,200)-(410,200)    no label, 10 in long             propagated 12x8; too short for two 12x8 elbows
    E   (410,200)-(410,300)    no label                         propagated 12x8
    H   (0,-300)-(150,-300)    "10X8"                           documented 10x8
    I   (150,-300)-(300,-300)  "8X8"                            documented 8x8 - a TRANSITION at (150,-300)
    F   (0,400)-(200,400) and G (0,440)-(200,440), one "6X6" at (100,420) between them: AMBIGUOUS
    X   M-EXHAUST (300,-60)-(300,80) "10X6" beside it           crosses C at (300,0): NOT joined
    "(500 CFM)" near A: an airflow, not a size

The truth file states what a correct reading, build and connection give; heights and systems are TEST values
of the spec that uses the drawing.
"""
import hashlib
import io
import json
import os
import subprocess
import sys

ENGINE = r'C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe'
TEMPLATE = r'C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\en-US\Template\acad.dwt'

LINES = [
    ('A', 'M-SUPPLY', (0, 0), (200, 0)), ('C', 'M-SUPPLY', (200, 0), (400, 0)),
    ('BR', 'M-SUPPLY', (200, 0), (200, -150)), ('L1', 'M-SUPPLY', (400, 0), (400, 200)),
    ('D', 'M-SUPPLY', (400, 200), (410, 200)), ('E', 'M-SUPPLY', (410, 200), (410, 300)),
    ('H', 'M-SUPPLY', (0, -300), (150, -300)), ('I', 'M-SUPPLY', (150, -300), (300, -300)),
    ('F', 'M-SUPPLY', (0, 400), (200, 400)), ('G', 'M-SUPPLY', (0, 440), (200, 440)),
    ('X', 'M-EXHAUST', (300, -60), (300, 80)),
]
TEXTS = [('16X8', (100, 10), 0), ('12X8', (330, 10), 0), ('8X6', (240, -100), 0), ('10X8', (75, -290), 0),
         ('8X8', (225, -290), 0), ('6X6', (100, 420), 0), ('10X6', (308, 50), 90), ('(500 CFM)', (60, -20), 0)]
LEADERS = [((200, -75), (238, -100))]
TRUTH = {
    'sizes_in': {'A': [16, 8], 'C': [12, 8], 'BR': [8, 6], 'L1': [12, 8], 'D': [12, 8], 'E': [12, 8],
                 'H': [10, 8], 'I': [8, 8], 'X': [10, 6]},
    'size_state': {'A': 'documented', 'C': 'documented', 'BR': 'documented (leader)', 'L1': 'propagated',
                   'D': 'propagated', 'E': 'propagated', 'H': 'documented', 'I': 'documented', 'X': 'documented',
                   'F': 'ambiguous', 'G': 'ambiguous'},
    'junctions': {'tee': [200, 0], 'elbows': [[400, 0], [400, 200], [410, 200]], 'transition': [150, -300]},
    'crossing_not_joined': [300, 0],
    'physically_doubtful': 'D is 10 in long between two 12x8 elbows: at least one elbow must be refused, with Revit\'s reason',
    'not_a_size': ['(500 CFM)'],
}


def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def layer(name, colour):
    return ('(entmake (list (cons 0 "LAYER") (cons 100 "AcDbSymbolTableRecord") (cons 100 "AcDbLayerTableRecord") '
            '(cons 2 "%s") (cons 70 0) (cons 62 %d)))' % (name, colour))


def main(folder, engine=ENGINE, template=TEMPLATE):
    os.makedirs(folder, exist_ok=True)
    dwg = os.path.join(folder, 'MEP-RECT.dwg')
    if os.path.exists(dwg):
        raise SystemExit('%s exists; it is never overwritten' % dwg)
    body = ['(setvar "FILEDIA" 0)', '(setvar "CMDDIA" 0)', '(setvar "INSUNITS" 1)',
            layer('M-SUPPLY', 4), layer('M-EXHAUST', 6), layer('M-TEXT', 2)]
    for _, lay, a, b in LINES:
        body.append('(entmake (list (cons 0 "LINE") (cons 8 "%s") (list 10 %s %s 0.0) (list 11 %s %s 0.0)))'
                    % (lay, float(a[0]), float(a[1]), float(b[0]), float(b[1])))
    for text, (x, y), rot in TEXTS:
        body.append('(entmake (list (cons 0 "TEXT") (cons 8 "M-TEXT") (list 10 %s %s 0.0) (cons 40 4.0) (cons 1 "%s") (cons 50 %s)))'
                    % (float(x), float(y), text, rot * 3.141592653589793 / 180))
    for (a, b) in LEADERS:
        body.append('(entmake (list (cons 0 "LEADER") (cons 100 "AcDbEntity") (cons 8 "M-TEXT") (cons 100 "AcDbLeader") '
                    '(cons 71 1) (cons 72 0) (cons 73 3) (cons 74 0) (cons 75 0) (cons 40 0.0) (cons 41 0.0) (cons 76 2) '
                    '(list 10 %s %s 0.0) (list 10 %s %s 0.0)))' % (float(a[0]), float(a[1]), float(b[0]), float(b[1])))
    body.append('(command "_.SAVEAS" "2018" "%s")' % dwg.replace('\\', '/'))
    scr = os.path.join(folder, 'MEP-RECT.scr')
    io.open(scr, 'w', encoding='utf-8', newline='\r\n').write('\n'.join(body) + '\n')
    r = subprocess.run([engine, '/i', template, '/s', scr, '/l', 'en-US'], capture_output=True, timeout=600)
    io.open(os.path.join(folder, 'MEP-RECT.log'), 'wb').write(r.stdout + r.stderr)
    if not os.path.exists(dwg):
        raise SystemExit('the console produced no MEP-RECT.dwg; see its log')
    out = {'units': 'inch', 'note': 'generated by make_mep_rect_dwg.py; a test drawing, not a project',
           'lines_in': LINES, 'texts_in': TEXTS, 'leaders_in': LEADERS, 'truth': TRUTH,
           'drawing': {'file': 'MEP-RECT.dwg', 'sha256': sha(dwg)}}
    io.open(os.path.join(folder, 'MEP-RECT.truth.json'), 'w', encoding='utf-8').write(json.dumps(out, indent=1))
    print(json.dumps(out['drawing']))


if __name__ == '__main__':
    args = sys.argv[1:]
    opts = {}
    while len(args) > 1 and args[-2].startswith('--'):
        opts[args[-2][2:]] = args[-1]
        args = args[:-2]
    main(args[0], opts.get('engine', ENGINE), opts.get('template', TEMPLATE))
