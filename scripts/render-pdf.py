"""Rasterise an exported PDF and list the text it actually carries.

The deliverable harnesses verify files and coordinates; a page still has to be
LOOKED AT. This turns each page into a PNG a person (or an agent) can read, and
dumps every word with its position on the paper in millimetres - so "the text is
there and not clipped" is something the record can show rather than assert.

It also reports every pair of words that PRINT ON TOP OF EACH OTHER, because a
reviewer looks at the crop they chose and the collision can be anywhere on the
sheet: 'overlaps' and 'overlap_count' per page, on the whole page, always.

    python render-pdf.py <pdf> <out-dir> [--dpi 200] [--clip x0,y0,x1,y1] [--zoom-dpi 600]

--clip is in PAPER MILLIMETRES from the top-left of the page and renders one
extra close-up at --zoom-dpi, for annotations too small to judge on a full sheet.
Requires PyMuPDF.
"""
import json
import os
import sys

import fitz  # PyMuPDF

MM_PER_PT = 25.4 / 72.0


def mm(v):
    return round(v * MM_PER_PT, 2)


# Two words whose boxes barely graze each other are ordinary typography - kerning,
# an underline, a box drawn a hair wide. Two words where one covers a FIFTH of the
# other are printing on top of each other. The threshold is the difference between
# reporting seven real collisions and reporting three hundred non-events; it was
# calibrated against pages that had both (2026-09-09).
OVERLAP_FRACTION = 0.2


def overlapping_pairs(words, fraction=OVERLAP_FRACTION):
    found = []
    for i in range(len(words)):
        ax0, ay0, ax1, ay1 = words[i]['mm']
        area_a = (ax1 - ax0) * (ay1 - ay0)
        for j in range(i + 1, len(words)):
            bx0, by0, bx1, by1 = words[j]['mm']
            wide = min(ax1, bx1) - max(ax0, bx0)
            tall = min(ay1, by1) - max(ay0, by0)
            if wide <= 0 or tall <= 0:
                continue
            area_b = (bx1 - bx0) * (by1 - by0)
            smaller = min(area_a, area_b)
            if smaller <= 0:
                continue
            share = (wide * tall) / smaller
            if share > fraction:
                found.append({
                    'a': words[i]['text'], 'a_mm': words[i]['mm'],
                    'b': words[j]['text'], 'b_mm': words[j]['mm'],
                    'share_of_smaller': round(share, 3),
                })
    return found


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    pdf, outdir = argv[1], argv[2]
    dpi = 200
    zoom_dpi = 600
    clip = None
    i = 3
    while i < len(argv):
        if argv[i] == '--dpi':
            dpi = int(argv[i + 1]); i += 2
        elif argv[i] == '--zoom-dpi':
            zoom_dpi = int(argv[i + 1]); i += 2
        elif argv[i] == '--clip':
            clip = [float(x) for x in argv[i + 1].split(',')]; i += 2
        else:
            i += 1
    os.makedirs(outdir, exist_ok=True)
    doc = fitz.open(pdf)
    report = {'pdf': os.path.abspath(pdf), 'pages': []}
    for index, page in enumerate(doc):
        rect = page.rect
        entry = {
            'page': index + 1,
            'paper_mm': [mm(rect.width), mm(rect.height)],
            'renders': [],
            'words': [],
        }
        scale = dpi / 72.0
        pix = page.get_pixmap(matrix=fitz.Matrix(scale, scale))
        full = os.path.join(outdir, 'page-%03d-%ddpi.png' % (index + 1, dpi))
        pix.save(full)
        entry['renders'].append({'png': os.path.abspath(full), 'dpi': dpi, 'px': [pix.width, pix.height]})
        if clip:
            box = fitz.Rect(clip[0] / MM_PER_PT, clip[1] / MM_PER_PT, clip[2] / MM_PER_PT, clip[3] / MM_PER_PT)
            zscale = zoom_dpi / 72.0
            zpix = page.get_pixmap(matrix=fitz.Matrix(zscale, zscale), clip=box)
            zoomed = os.path.join(outdir, 'page-%03d-clip-%ddpi.png' % (index + 1, zoom_dpi))
            zpix.save(zoomed)
            entry['renders'].append({'png': os.path.abspath(zoomed), 'dpi': zoom_dpi,
                                     'clip_mm': clip, 'px': [zpix.width, zpix.height]})
        # Every word and where it sits on the paper. A word present here is a word
        # the PDF carries; a word whose box runs past the page is a word cut off.
        for w in page.get_text('words'):
            x0, y0, x1, y1, text = w[0], w[1], w[2], w[3], w[4]
            entry['words'].append({
                'text': text,
                'mm': [mm(x0), mm(y0), mm(x1), mm(y1)],
                'off_page': bool(x0 < 0 or y0 < 0 or x1 > rect.width or y1 > rect.height),
            })
        entry['word_count'] = len(entry['words'])
        entry['off_page_words'] = [w['text'] for w in entry['words'] if w['off_page']]
        # AND WHETHER ANY OF IT PRINTS ON TOP OF ANYTHING ELSE. "No word ran off the
        # paper" and "no word sits on another" are two different questions, and this
        # file used to answer only the first - which let a sheet name print straight
        # through the titleblock across five accepted pages before anybody noticed
        # (2026-09-09). A reviewer choosing a crop cannot see what is outside it;
        # this looks at the whole page every time.
        entry['overlaps'] = overlapping_pairs(entry['words'])
        entry['overlap_count'] = len(entry['overlaps'])
        report['pages'].append(entry)
    out = os.path.join(outdir, 'render-report.json')
    with open(out, 'w', encoding='utf-8') as fh:
        json.dump(report, fh, indent=1)
    print(json.dumps({'report': os.path.abspath(out),
                      'pages': [{'page': p['page'], 'paper_mm': p['paper_mm'],
                                 'words': p['word_count'], 'off_page': p['off_page_words'],
                                 'overlaps': [(o['a'], o['b'], o['share_of_smaller'])
                                              for o in p['overlaps']],
                                 'renders': [r['png'] for r in p['renders']]}
                                for p in report['pages']]}, indent=1))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
