import os, re, subprocess, sys
# Audit 2026-10-08, lote 7, live: builds a SYNTHETIC DWG with accoreconsole (a mirrored arc, a mirrored insert, a nested
# block whose parent is based at (5,5)), then runs the extractor's embedded forms on it
# (usage: dwg-ocs-fixture.py <CadDwgScript.cs> [outdir]). Expected records: X for the
# mirrored arc and insert, K HZPARENT with base 5,5. Measured 2026-10-08 with AutoCAD 2025;
# the reader turned them into the world positions CadOcsAndBlockBaseTests assert.
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.environ.get("TEMP", "."), "hz-dwg-ocs")
os.makedirs(out, exist_ok=True)
acc = os.environ.get("HZ_ACCORECONSOLE", r"C:\Program Files\Autodesk\AutoCAD 2025\accoreconsole.exe")
dwg = out + r"\hz-ocs.dwg"
tsv = out + r"\hz-ocs.tsv"
for f in (dwg, tsv):
    if os.path.exists(f): os.remove(f)

build = [
    '(setvar "INSUNITS" 4)',
    "(entmake '((0 . \"ARC\") (8 . \"HZ-ARC\") (10 10.0 0.0 0.0) (40 . 5.0) (50 . 0.0) (51 . 1.5707963) (210 0.0 0.0 -1.0)))",
    "(entmake '((0 . \"ARC\") (8 . \"HZ-ARC-PLAIN\") (10 10.0 0.0 0.0) (40 . 5.0) (50 . 0.0) (51 . 1.5707963)))",
    "(entmake '((0 . \"BLOCK\") (2 . \"HZCHILD\") (70 . 0) (10 0.0 0.0 0.0)))",
    "(entmake '((0 . \"LINE\") (8 . \"0\") (10 0.0 0.0 0.0) (11 1.0 0.0 0.0)))",
    "(entmake '((0 . \"ENDBLK\")))",
    "(entmake '((0 . \"BLOCK\") (2 . \"HZPARENT\") (70 . 0) (10 5.0 5.0 0.0)))",
    "(entmake '((0 . \"INSERT\") (8 . \"0\") (2 . \"HZCHILD\") (10 5.0 5.0 0.0)))",
    "(entmake '((0 . \"ENDBLK\")))",
    "(entmake '((0 . \"INSERT\") (8 . \"HZ-SYM\") (2 . \"HZPARENT\") (10 1000.0 2000.0 0.0)))",
    "(entmake '((0 . \"INSERT\") (8 . \"HZ-SYM\") (2 . \"HZCHILD\") (10 300.0 0.0 0.0) (50 . 0.5) (210 0.0 0.0 -1.0)))",
    '(command "_.SAVEAS" "2018" "' + dwg.replace("\\", "/") + '")',
    '(princ "\\nHZFIXTURE-SAVED\\n")',
]
scr1 = out + r"\build.scr"
open(scr1, "w", encoding="ascii", newline="\r\n").write("\n".join(build) + "\n")
r1 = subprocess.run([acc, "/s", scr1, "/l", "en-US"], capture_output=True, timeout=300)
print("build exit", r1.returncode, "dwg exists", os.path.exists(dwg))

# The extractor exactly as the bridge embeds it.
src = open(sys.argv[1], encoding="utf-8-sig").read()
forms = re.findall(r'^\s*@"(.*)",\s*$', src, re.M)
forms = [f.replace('""', '"') for f in forms]
scr2 = out + r"\extract.scr"
lines = forms + ['(hz-dump "' + tsv.replace("\\", "/") + '")']
open(scr2, "w", encoding="ascii", newline="\r\n").write("\n".join(lines) + "\n")
r2 = subprocess.run([acc, "/i", dwg, "/s", scr2, "/l", "en-US"], capture_output=True, timeout=300)
print("extract exit", r2.returncode, "tsv exists", os.path.exists(tsv), "forms", len(forms))
if os.path.exists(tsv):
    for l in open(tsv, encoding="utf-8", errors="replace"):
        if l.startswith(("X\t", "K\t")) or "\tARC\t" in l or "\tINSERT\t" in l:
            print(l.rstrip())
