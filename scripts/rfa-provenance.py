"""Read what a Revit family file says about ITSELF, without opening Revit.

An .rfa is an OLE compound file. The stream Revit calls BasicFileInfo holds
UTF-16 text with the version that last saved it ("Format: 2023", "Revit Build:
..."), which is exactly the provenance a fixture question needs: a family saved
by a later Revit never opens in an earlier one.

The family CATEGORY is not published as text there, so this also scans the file
for the UTF-16 category names that matter here and reports what it found - as a
HINT, explicitly, because a string inside a binary is not the same fact as
Revit's own answer. Anything this cannot decide is printed as
"pending validation", never as a category.

    python rfa-provenance.py <file-or-directory> [...]
"""
import json
import os
import re
import sys

# The categories a wall can be tagged with, plus the ones worth telling apart
# from them when a name is suggestive.
CATEGORY_HINTS = [
    'Multi-Category Tags', 'Multi Category Tags', 'Wall Tags', 'Room Tags', 'Door Tags',
    'Window Tags', 'Generic Annotations', 'Structural Precast Tags', 'Viewports',
    'Schedule Graphics', 'Generic Model', 'Detail Items', 'Casework',
]
VERSION_RE = re.compile(r'(Format|Build|Revit Build|Last Save Path|Locale)\s*:\s*([^\r\n\x00]{0,120})')
YEAR_RE = re.compile(r'\b(20\d\d)\b')


def utf16_text(data):
    # Decode as UTF-16LE and keep what survives; Revit writes its BasicFileInfo
    # that way, and the rest of the file simply produces noise we filter.
    try:
        return data.decode('utf-16-le', errors='ignore')
    except Exception:
        return ''


# A project file is tens of megabytes and BasicFileInfo sits near its start;
# decoding the whole thing twice just to read a version number takes minutes.
MAX_READ = 8 * 1024 * 1024


def inspect(path):
    out = {'path': os.path.abspath(path), 'size': os.path.getsize(path)}
    with open(path, 'rb') as fh:
        data = fh.read(MAX_READ)
    out['read_bytes'] = len(data)
    out['is_ole_compound'] = data[:8] == b'\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1'
    # Both alignments: UTF-16LE text can start on an odd byte inside a stream.
    text = utf16_text(data) + '\n' + utf16_text(data[1:])
    fields = {}
    for key, value in VERSION_RE.findall(text):
        value = value.strip()
        if value and key not in fields:
            fields[key] = value
    out['file_info'] = fields
    saved_by = None
    for key in ('Format', 'Revit Build', 'Build'):
        if key in fields:
            found = YEAR_RE.search(fields[key])
            if found:
                saved_by = int(found.group(1))
                break
    out['saved_by_revit'] = saved_by
    out['opens_in_2023'] = (saved_by <= 2023) if saved_by else None
    hits = []
    for name in CATEGORY_HINTS:
        if name in text:
            hits.append(name)
    out['category_strings_found'] = hits
    out['category'] = 'pending validation (a string in a binary is not Revit\'s answer)'
    return out


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    targets = []
    for arg in argv[1:]:
        if os.path.isdir(arg):
            for root, _dirs, files in os.walk(arg):
                for name in files:
                    if name.lower().endswith('.rfa'):
                        targets.append(os.path.join(root, name))
        else:
            targets.append(arg)
    report = []
    for target in targets:
        try:
            report.append(inspect(target))
        except Exception as exc:  # a file that cannot be read is a finding, not a crash
            report.append({'path': os.path.abspath(target), 'error': str(exc)})
    print(json.dumps(report, indent=1, ensure_ascii=False))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
