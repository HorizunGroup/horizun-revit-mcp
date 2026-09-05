# -----------------------------------------------------------------------------
# Horizun Revit MCP - original Horizun code.
#
# The extension icon, GENERATED rather than committed as an opaque blob.
#
# It is the same mark the Revit ribbon already carries (Resources/hub32.png): a
# teal disc with a white H. That file is 32x32 and an extension gallery shows a
# much larger tile, so this renders the mark at 128 and 256 from geometry instead
# of upscaling 32 pixels into mush.
#
#   python integrations/claude-desktop/make-icon.py
#
# Deterministic: the same source produces the same bytes, which is what lets the
# packaging gate assert the committed PNGs are the ones this script draws.
# -----------------------------------------------------------------------------
import io
import os
import struct
import sys
import zlib

# Measured from src/Horizun.Revit/Resources/hub32.png: the disc runs from a light
# teal at the top to a darker one at the bottom, and the H is pure white.
TOP = (18, 106, 136)
BOTTOM = (9, 62, 86)
WHITE = (255, 255, 255)

# Proportions of the 32x32 mark, as fractions of the tile, so every size is the
# same drawing rather than a differently-cropped one.
DISC_R = 14.0 / 32.0          # disc radius
STEM_W = 2.0 / 32.0           # thickness of each upright of the H
STEM_L = -5.0 / 32.0          # left upright, centre offset from the middle
STEM_R = 5.0 / 32.0           # right upright
BAR_H = 1.0 / 32.0            # half-height of the crossbar
GLYPH_H = 6.5 / 32.0          # half-height of the uprights


def _coverage(cx, cy, inside, samples=4):
    """Analytic anti-aliasing by supersampling one pixel."""
    hit = 0
    step = 1.0 / samples
    for sy in range(samples):
        for sx in range(samples):
            if inside(cx + (sx + 0.5) * step, cy + (sy + 0.5) * step):
                hit += 1
    return hit / float(samples * samples)


def render(size):
    n = float(size)
    mid = n / 2.0
    r = DISC_R * n
    stem_w = STEM_W * n
    glyph_h = GLYPH_H * n
    bar_h = BAR_H * n

    def in_disc(x, y):
        dx, dy = x - mid, y - mid
        return dx * dx + dy * dy <= r * r

    def in_glyph(x, y):
        dx, dy = x - mid, y - mid
        if abs(dy) <= bar_h and abs(dx) <= (STEM_R * n + stem_w / 2.0):
            return True
        if abs(dy) <= glyph_h:
            for c in (STEM_L * n, STEM_R * n):
                if abs(dx - c) <= stem_w / 2.0:
                    return True
        return False

    rows = []
    for y in range(size):
        row = bytearray()
        # Vertical gradient across the disc, exactly as the 32px mark reads.
        t = y / (n - 1.0)
        base = tuple(int(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t)) for i in range(3))
        for x in range(size):
            disc = _coverage(x, y, in_disc)
            if disc <= 0.0:
                row += bytes((0, 0, 0, 0))
                continue
            glyph = _coverage(x, y, in_glyph)
            colour = tuple(
                int(round(base[i] + (WHITE[i] - base[i]) * glyph)) for i in range(3)
            )
            row += bytes(colour + (int(round(255 * disc)),))
        rows.append(bytes(row))
    return rows


def write_png(path, size):
    rows = render(size)
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    # Fixed compression level and no timestamp chunk: same input, same bytes.
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as fh:
        fh.write(png)
    return len(png)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    check = "--check" in sys.argv
    problems = []
    for size in (128, 256):
        path = os.path.join(here, "icon-%d.png" % size)
        if check:
            rendered = io.BytesIO()
            tmp = path + ".rendered"
            write_png(tmp, size)
            with open(tmp, "rb") as fh:
                rendered = fh.read()
            os.remove(tmp)
            if not os.path.exists(path):
                problems.append("%s does not exist" % os.path.basename(path))
            elif open(path, "rb").read() != rendered:
                problems.append("%s is not what this script draws" % os.path.basename(path))
        else:
            n = write_png(path, size)
            print("wrote %s (%d bytes)" % (path, n))
    if check:
        if problems:
            sys.exit("icon check failed: " + "; ".join(problems))
        print("[PASS] the committed icons are the ones make-icon.py draws")


if __name__ == "__main__":
    main()
