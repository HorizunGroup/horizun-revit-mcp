# -*- coding: utf-8 -*-
"""An ADVERSARIAL SYNTHETIC drawing for section changes - generated, never copied from a project.

    python make_mep_sections_dwg.py <out_folder> [--engine <accoreconsole.exe>] [--template <acad.dwt>]

Writes <out_folder>/rev0/MEP-SECT.dwg and <out_folder>/rev1/MEP-SECT.dwg (a revision of it), each with a
.tsv dump in the reader-independent format and a truth file. Refuses to overwrite. Inches, single-line
centrelines on M-SUPPLY, sizes as TEXT on M-TEXT. Frozen truth, before any product run:

  S1  (0,0)-(400,0)        "10X8" at x=60, "8X8" at x=300, branch B1 (200,-2.5)-(200,-150) taps it
                           -> cut at the tap (x=200): 10X8 | 8X8; B1 missing (no label)
  S2  (0,-300)-(400,-300)  "12X8" at x=60, "8X6" at x=330, nothing between
                           -> 12X8 end (to x=60), change_unlocated (60..330), 8X6 end (from 330)
  S3  (0,-600)-(150,-600) "12X8"; T (150,-600)-(161,-600) drawn transition; M (161,-600)-(300,-600) no label;
      V (300,-600)-(300,-400) "8X8" -> T transition 12X8->8X8, M propagated 8X8 (through the elbow)
  L   H (0,-900)-(200,-900) "8X6" at x=100; U (200,-900)-(200,-700) no label -> U propagated 8X6; elbow at (200,-900)
  S4  (0,-1200)-(400,-1200) "8X8" at x=60 only -> documented whole

  rev1: L's label "8X6" -> "10X6" (both legs resized, the elbow's legs 200 in: a rebuilt elbow fits);
        S2's "8X6" moves from x=330 to x=300 (its pieces keep their names; the bound moves);
        S4 gains "8X6" at x=330 (a revision ADDS a division).
Heights and systems are TEST values of the spec that uses the drawing.
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
    ('S1', (0, 0), (400, 0)), ('B1', (200, -2.5), (200, -150)),
    ('S2', (0, -300), (400, -300)),
    ('S3a', (0, -600), (150, -600)), ('T', (150, -600), (161, -600)), ('M', (161, -600), (300, -600)),
    ('V', (300, -600), (300, -400)),
    ('H', (0, -900), (200, -900)), ('U', (200, -900), (200, -700)),
    ('S4', (0, -1200), (400, -1200)),
]
TEXTS0 = [('10X8', (60, 8), 0), ('8X8', (300, 8), 0), ('12X8', (60, -292), 0), ('8X6', (330, -292), 0),
          ('12X8', (60, -592), 0), ('8X8', (308, -500), 90), ('8X6', (100, -892), 0), ('8X8', (60, -1192), 0)]
TEXTS1 = [('10X8', (60, 8), 0), ('8X8', (300, 8), 0), ('12X8', (60, -292), 0), ('8X6', (300, -292), 0),
          ('12X8', (60, -592), 0), ('8X8', (308, -500), 90), ('10X6', (100, -892), 0), ('8X8', (60, -1192), 0),
          ('8X6', (330, -1192), 0)]
TRUTH = {
    'rev0': {'S1': 'cut at the tap x=200: 10X8 | 8X8', 'B1': 'missing', 'S2': '12X8 end | change_unlocated 60..330 | 8X6 end',
             'S3a': 'documented 12X8', 'T': 'transition 12X8->8X8', 'M': 'propagated 8X8', 'V': 'documented 8X8',
             'H': 'documented 8X6', 'U': 'propagated 8X6', 'S4': 'documented 8X8 whole',
             'counts': {'documented': 8, 'propagated': 2, 'transition': 1, 'change_unlocated': 1, 'missing': 1},
             'ducts_built': 10},
    'rev1': {'H': 'resized 8X6 -> 10X6', 'U': 'resized 8X6 -> 10X6 (propagated)', 'elbow_H_U': 'rebuild viable (legs 200 in)',
             'S2': 'same piece keys; the 8X6 end and the unlocated piece move their bound from 330 to 300',
             'S4': 'cut into 8X8 end | unlocated 60..330 | 8X6 end (a division added)'},
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


def one(folder, texts, engine, template):
    os.makedirs(folder, exist_ok=True)
    dwg = os.path.join(folder, 'MEP-SECT.dwg')
    if os.path.exists(dwg):
        raise SystemExit('%s exists; it is never overwritten' % dwg)
    body = ['(setvar "FILEDIA" 0)', '(setvar "CMDDIA" 0)', '(setvar "INSUNITS" 1)', layer('M-SUPPLY', 4), layer('M-TEXT', 2)]
    tsv = []
    for i, (name, a, b) in enumerate(LINES):
        body.append('(entmake (list (cons 0 "LINE") (cons 8 "M-SUPPLY") (list 10 %s %s 0.0) (list 11 %s %s 0.0)))'
                    % (float(a[0]), float(a[1]), float(b[0]), float(b[1])))
        tsv.append('\t'.join(['E', '*Model_Space', 'L%d' % i, 'LINE', 'M-SUPPLY', '2', '0', '%s,%s;%s,%s' % (a[0], a[1], b[0], b[1])]))
    for i, (text, (x, y), rot) in enumerate(texts):
        body.append('(entmake (list (cons 0 "TEXT") (cons 8 "M-TEXT") (list 10 %s %s 0.0) (cons 40 4.0) (cons 1 "%s") (cons 50 %s)))'
                    % (float(x), float(y), text, rot * 3.141592653589793 / 180))
        tsv.append('\t'.join(['E', '*Model_Space', 'T%d' % i, 'TEXT', 'M-TEXT', str(x), str(y), '', text]))
    body.append('(command "_.SAVEAS" "2018" "%s")' % dwg.replace('\\', '/'))
    scr = os.path.join(folder, 'MEP-SECT.scr')
    io.open(scr, 'w', encoding='utf-8', newline='\r\n').write('\n'.join(body) + '\n')
    r = subprocess.run([engine, '/i', template, '/s', scr, '/l', 'en-US'], capture_output=True, timeout=600)
    io.open(os.path.join(folder, 'MEP-SECT.log'), 'wb').write(r.stdout + r.stderr)
    if not os.path.exists(dwg):
        raise SystemExit('the console produced no MEP-SECT.dwg; see its log')
    io.open(os.path.join(folder, 'MEP-SECT.tsv'), 'w', encoding='utf-8').write('\n'.join(tsv) + '\n')
    return {'file': dwg, 'sha256': sha(dwg)}


def main(folder, engine=ENGINE, template=TEMPLATE):
    out = {'units': 'inch', 'note': 'generated by make_mep_sections_dwg.py; an ADVERSARIAL SYNTHETIC test drawing, not a project',
           'lines_in': LINES, 'texts_rev0': TEXTS0, 'texts_rev1': TEXTS1, 'truth': TRUTH,
           'rev0': one(os.path.join(folder, 'rev0'), TEXTS0, engine, template),
           'rev1': one(os.path.join(folder, 'rev1'), TEXTS1, engine, template)}
    io.open(os.path.join(folder, 'MEP-SECT.truth.json'), 'w', encoding='utf-8').write(json.dumps(out, indent=1))
    print(json.dumps({'rev0': out['rev0'], 'rev1': out['rev1']}))


if __name__ == '__main__':
    args = sys.argv[1:]
    opts = {}
    while len(args) > 1 and args[-2].startswith('--'):
        opts[args[-2][2:]] = args[-1]
        args = args[:-2]
    main(args[0], opts.get('engine', ENGINE), opts.get('template', TEMPLATE))
