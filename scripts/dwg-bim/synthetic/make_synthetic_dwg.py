# -*- coding: utf-8 -*-
"""A SYNTHETIC units drawing for the DWG -> BIM route, generated - never copied from a project.

    python make_synthetic_dwg.py <out_folder> [--engine <accoreconsole.exe>] [--template <acad.dwt>]

Writes <out_folder>/SYNTH-UNIT.dwg (inches) and SYNTH-UNIT.truth.json - what the drawing states,
for the acceptance of a run. Everything is drawn by entmake from this file; nothing is read from any
other drawing. Refuses to overwrite an existing SYNTH-UNIT.dwg.

Content (inches, origin 0,0):
  walls on A-WALL, drawn as pairs of face lines:
    W-S  south   y 0..8,     x 0..240      8 in
    W-N  north   y 176..184, x 0..240      8 in
    W-W  west    x 0..8,     y 8..176      8 in
    W-E  east    x 232..240, y 8..176      8 in
    W-P  partition x 116..124, y 8..100    8 in, FREE end at y 100 (a cap line closes it)
    W-5  stub    x 170..175, y 120..176    5 in - a thickness the example catalogue does not list
  blocks OUT on E-P (a circle and two prongs along +y; block_facing OUT = +y):
    R1 (60, 14)      rot 0     in front of W-S's inner face
    R2 (226, 90)     rot 90    in front of W-E's inner face
    R3 (110, 50)     rot 90    in front of W-P's west face
    R4 (120, 104)    rot 0     in front of W-P's FREE END (hosted only where the rule allows end faces)
    R5 (172.5, 110)  rot 0     in front of the 5 in stub's end - withdrawn with its wall
    R6 (190, 60)     rot 0     in the middle of the room - no wall carries it
"""
import hashlib
import io
import json
import os
import subprocess
import sys

ENGINE = r'C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe'
TEMPLATE = r'C:\Program Files\Autodesk\AutoCAD 2025\UserDataCache\en-US\Template\acad.dwt'

WALLS = {
    'W-S': [((0, 0), (240, 0)), ((0, 8), (240, 8))],
    'W-N': [((0, 176), (240, 176)), ((0, 184), (240, 184))],
    'W-W': [((0, 8), (0, 176)), ((8, 8), (8, 176))],
    'W-E': [((232, 8), (232, 176)), ((240, 8), (240, 176))],
    'W-P': [((116, 8), (116, 100)), ((124, 8), (124, 100)), ((116, 100), (124, 100))],
    'W-5': [((170, 120), (170, 176)), ((175, 120), (175, 176)), ((170, 120), (175, 120))],
}
SYMBOLS = [
    ('R1', 60, 14, 0, 'W-S', 'side'),
    ('R2', 226, 90, 90, 'W-E', 'side'),
    ('R3', 110, 50, 90, 'W-P', 'side'),
    ('R4', 120, 104, 0, 'W-P', 'end'),
    ('R5', 172.5, 110, 0, 'W-5', 'end'),
    ('R6', 190, 60, 0, None, None),
]


def lisp():
    out = ['(setvar "FILEDIA" 0)', '(setvar "CMDDIA" 0)', '(setvar "INSUNITS" 1)',
           '(entmake (list (cons 0 "LAYER") (cons 100 "AcDbSymbolTableRecord") (cons 100 "AcDbLayerTableRecord") (cons 2 "A-WALL") (cons 70 0) (cons 62 1)))',
           '(entmake (list (cons 0 "LAYER") (cons 100 "AcDbSymbolTableRecord") (cons 100 "AcDbLayerTableRecord") (cons 2 "E-P") (cons 70 0) (cons 62 5)))',
           # the block: a circle and two prongs along +y, defined on layer 0
           '(entmake (list (cons 0 "BLOCK") (cons 2 "OUT") (cons 70 0) (list 10 0.0 0.0 0.0)))',
           '(entmake (list (cons 0 "CIRCLE") (cons 8 "0") (list 10 0.0 0.0 0.0) (cons 40 3.0)))',
           '(entmake (list (cons 0 "LINE") (cons 8 "0") (list 10 -1.0 3.0 0.0) (list 11 -1.0 5.0 0.0)))',
           '(entmake (list (cons 0 "LINE") (cons 8 "0") (list 10 1.0 3.0 0.0) (list 11 1.0 5.0 0.0)))',
           '(entmake (list (cons 0 "ENDBLK")))']
    for lines in WALLS.values():
        for (a, b) in lines:
            out.append('(entmake (list (cons 0 "LINE") (cons 8 "A-WALL") (list 10 %s %s 0.0) (list 11 %s %s 0.0)))'
                       % (float(a[0]), float(a[1]), float(b[0]), float(b[1])))
    for (_, x, y, rot, _, _) in SYMBOLS:
        out.append('(entmake (list (cons 0 "INSERT") (cons 2 "OUT") (cons 8 "E-P") (list 10 %s %s 0.0) (cons 50 %s)))'
                   % (float(x), float(y), rot * 3.141592653589793 / 180.0))
    return out


def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def main(folder, engine=ENGINE, template=TEMPLATE):
    os.makedirs(folder, exist_ok=True)
    dwg = os.path.join(folder, 'SYNTH-UNIT.dwg')
    if os.path.exists(dwg):
        raise SystemExit('%s exists; it is never overwritten' % dwg)
    scr = os.path.join(folder, 'make-synth.scr')
    body = lisp() + ['(command "_.SAVEAS" "2018" "%s")' % dwg.replace('\\', '/')]
    io.open(scr, 'w', encoding='utf-8', newline='\r\n').write('\n'.join(body) + '\n')
    r = subprocess.run([engine, '/i', template, '/s', scr, '/l', 'en-US'], capture_output=True, timeout=600)
    io.open(os.path.join(folder, 'make-synth.log'), 'wb').write(r.stdout + r.stderr)
    if not os.path.exists(dwg):
        raise SystemExit('the console produced no drawing; see make-synth.log')
    truth = {
        'drawing': os.path.basename(dwg), 'sha256': sha(dwg), 'units': 'inch',
        'walls': {k: {'lines_in': v} for k, v in WALLS.items()},
        'symbols': [{'id': s[0], 'at_in': [s[1], s[2]], 'rotation_degrees': s[3], 'wall': s[4], 'face': s[5]}
                    for s in SYMBOLS],
        'note': 'generated by make_synthetic_dwg.py; a test drawing, not a project'}
    io.open(os.path.join(folder, 'SYNTH-UNIT.truth.json'), 'w', encoding='utf-8').write(json.dumps(truth, indent=1))
    print(json.dumps({'dwg': dwg, 'sha256': truth['sha256'], 'exit_code': r.returncode}, indent=1))


if __name__ == '__main__':
    args = sys.argv[1:]
    opts = {}
    while len(args) > 1 and args[-2].startswith('--'):
        opts[args[-2][2:]] = args[-1]
        args = args[:-2]
    main(args[0], opts.get('engine', ENGINE), opts.get('template', TEMPLATE))
