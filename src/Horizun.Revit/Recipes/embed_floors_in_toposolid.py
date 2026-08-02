# -*- coding: utf-8 -*-
# -----------------------------------------------------------------------------
# Horizun MCP - original Horizun code.
#
# EMBED FLOORS INTO A TOPOSOLID, TOP FACE FLUSH WITH THE GRASS.
#
# Ported from the "Nivelar Topo V2" pyRevit button. Slabs sit ON a toposolid; this
# makes the ground accept them - the slab's top face stays flush and its body goes
# into the terrain. Around each slab it writes three rings of shape points: the
# boundary itself and an outer ring at the top-face elevation, and an inner ring
# one centimetre in at the slab's UNDERSIDE, which is what pulls the terrain down
# around it.
#
# The algorithm is the button's, and it is careful in ways worth keeping:
#
#   * Slabs that TOUCH and sit at the SAME elevation are merged into one outline
#     by 2D edge cancellation - shared edges annul - so no split line is drawn
#     along a false seam. Slabs that touch with a real STEP between them are NOT
#     merged, because that step is a design feature and smoothing it away would
#     be wrong. No solid booleans: Revit's kernel fails on slabs meeting edge to
#     edge, which is exactly the case this has to handle.
#   * Arcs are tessellated, so the ring follows curves.
#   * Corners are mitred, so a rectangular slab keeps sharp corners.
#   * A sloped top face is sampled per point off its plane equation, so ramps work.
#   * IT NEVER ITERATES SlabShapeCreases. That read crashes Revit on large
#     toposolids. The verification below counts VERTICES for the same reason -
#     the honesty contract does not get to crash the host to satisfy itself.
#
# WHAT CHANGED IN THE PORT:
#
#   1. The two prompted numbers (outer offset, point spacing) are arguments.
#   2. doc is the host's, not a module global read from pyRevit.
#   3. The toposolid was resolved by "the only one in the document, or click".
#      Here toposolid_id is explicit; omitted, it is auto-resolved ONLY when the
#      document holds exactly one, and the choice is REPORTED. Ambiguity is
#      refused with the candidates listed rather than resolved by guessing.
#   4. Element ids no longer go through the deprecated IntegerValue.
#   5. verify() RECOMPUTES the rings after the commit and asks the model whether
#      a vertex is really at each position - an independent re-read, not a count
#      of AddPoint calls that did not throw.
#
# No Transaction in this file. The host owns the commit - see Recipe.cs.
# -----------------------------------------------------------------------------
from __future__ import division

import math

from Autodesk.Revit.DB import (
    ElementId, FilteredElementCollector, Floor, HostObjectUtils, PlanarFace,
    Toposolid, UnitTypeId, UnitUtils, XYZ
)

import hz

# The constants the button did not prompt for. Left as constants deliberately:
# they describe how the profile is built, not what the caller is choosing.
DEFAULT_INNER_CM = 1.0       # inner ring, at the slab underside - this embeds it
CLEAN_NEAR_CM = 60.0         # radius for clearing stale points around the edge
ADJ_PERP_MM = 8.0            # perpendicular tolerance for "same edge"
ADJ_OVERLAP_MM = 15.0        # minimum overlap to call two slabs adjacent
ADJ_ANGLE = 0.02             # ~1.1 degrees
ADJ_ELEV_MM = 10.0           # elevation tolerance at the contact point: above this
                             # it is a real step and the slabs are NOT merged
DUP_TOL_MM = 5.0             # vertex deduplication tolerance
MITER_LIMIT = 5.0            # miter limit, so a sharp corner does not spike


def cm(v):
    return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Centimeters)


def mm(v):
    return UnitUtils.ConvertToInternalUnits(v, UnitTypeId.Millimeters)


# --------------------------------------------------------------- 2D geometry

def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def sarea(pts):
    a = 0.0
    n = len(pts)
    for i in range(n):
        a += (pts[i][0] * pts[(i + 1) % n][1]) - (pts[(i + 1) % n][0] * pts[i][1])
    return a * 0.5


def floor_all_loops(doc, floor):
    """Every loop of the slab's sketch (outer plus holes), tessellated so arcs
    become fine polylines and the ring follows the curves."""
    out = []
    try:
        sketch = doc.GetElement(floor.SketchId)
        profile = sketch.Profile
    except Exception:
        return out
    if profile is None:
        return out
    for carr in profile:
        pts = []
        for c in carr:
            try:
                tess = [(p.X, p.Y) for p in c.Tessellate()]
            except Exception:
                continue
            pts.extend(tess if not pts else tess[1:])
        if pts and dist(pts[0], pts[-1]) < 1e-6:
            pts = pts[:-1]
        if len(pts) >= 3:
            out.append(pts)
    return out


def floor_segments(doc, floor):
    segs = []
    for lp in floor_all_loops(doc, floor):
        n = len(lp)
        for i in range(n):
            segs.append((lp[i], lp[(i + 1) % n]))
    return segs


def _collinear_overlap(sa, sb, perp, ang):
    (ax, ay), (bx, by) = sa
    dx, dy = bx - ax, by - ay
    L = math.hypot(dx, dy)
    if L < 1e-9:
        return 0.0, None
    ux, uy = dx / L, dy / L
    nx, ny = -uy, ux
    for (px, py) in (sb[0], sb[1]):
        if abs((px - ax) * nx + (py - ay) * ny) > perp:
            return 0.0, None
    ex, ey = sb[1][0] - sb[0][0], sb[1][1] - sb[0][1]
    Lb = math.hypot(ex, ey)
    if Lb < 1e-9:
        return 0.0, None
    if abs(ux * (ey / Lb) - uy * (ex / Lb)) > ang:
        return 0.0, None
    t0 = (sb[0][0] - ax) * ux + (sb[0][1] - ay) * uy
    t1 = (sb[1][0] - ax) * ux + (sb[1][1] - ay) * uy
    lo = max(0.0, min(t0, t1))
    hi = min(L, max(t0, t1))
    overlap = hi - lo
    if overlap <= 0.0:
        return 0.0, None
    mid_t = 0.5 * (lo + hi)
    return overlap, (ax + ux * mid_t, ay + uy * mid_t)


def _floor_plane(floor):
    """Plane equation of the largest planar top face, or None."""
    bestf = None
    ba = -1.0
    try:
        for r in HostObjectUtils.GetTopFaces(floor):
            fc = floor.GetGeometryObjectFromReference(r)
            if isinstance(fc, PlanarFace) and fc.Area > ba:
                ba = fc.Area
                bestf = fc
    except Exception:
        pass
    if bestf is not None and abs(bestf.FaceNormal.Z) > 1e-6:
        n = bestf.FaceNormal
        o = bestf.Origin
        return (n.X, n.Y, n.Z, o.X, o.Y, o.Z)
    return None


def _plane_z(plane, x, y, fallback):
    if plane is None:
        return fallback
    nx, ny, nz, ox, oy, oz = plane
    return oz - (nx * (x - ox) + ny * (y - oy)) / nz


def floors_adjacent(segs_a, segs_b, plane_a, fallback_a, plane_b, fallback_b):
    """Touching AND level at the contact point. A real step there returns False on
    purpose: it is a design feature, not a seam to smooth away."""
    perp = mm(ADJ_PERP_MM)
    ov = mm(ADJ_OVERLAP_MM)
    elev_tol = mm(ADJ_ELEV_MM)
    for sa in segs_a:
        for sb in segs_b:
            overlap, mid = _collinear_overlap(sa, sb, perp, ADJ_ANGLE)
            if overlap < ov or mid is None:
                continue
            za = _plane_z(plane_a, mid[0], mid[1], fallback_a)
            zb = _plane_z(plane_b, mid[0], mid[1], fallback_b)
            if abs(za - zb) <= elev_tol:
                return True
    return False


def group_floors(doc, floors):
    """Union-Find over slabs that touch AND are level where they touch."""
    segs = [floor_segments(doc, f) for f in floors]
    planes = [_floor_plane(f) for f in floors]
    fallbacks = []
    for f in floors:
        bb = f.get_BoundingBox(None)
        fallbacks.append(bb.Max.Z if bb else 0.0)

    n = len(floors)
    parent = list(range(n))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(i, j):
        ri, rj = find(i), find(j)
        if ri != rj:
            parent[rj] = ri

    for i in range(n):
        for j in range(i + 1, n):
            if find(i) == find(j):
                continue
            if floors_adjacent(segs[i], segs[j], planes[i], fallbacks[i],
                               planes[j], fallbacks[j]):
                union(i, j)

    groups = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(floors[i])
    return list(groups.values())


def merged_boundary(doc, group):
    """The merged outer outline of a group, in 2D, by edge cancellation: an edge
    shared by two different slabs is an internal seam and annuls. No solid
    booleans - Revit's kernel fails on slabs that meet edge to edge."""
    RK = 0.00164  # ~0.5mm in feet
    PERP = mm(ADJ_PERP_MM)

    def rk(p):
        return (int(round(p[0] / RK)), int(round(p[1] / RK)))

    def param_on(a, b, v):
        dx, dy = b[0] - a[0], b[1] - a[1]
        L2 = dx * dx + dy * dy
        if L2 < 1e-12:
            return None
        t = ((v[0] - a[0]) * dx + (v[1] - a[1]) * dy) / L2
        if t <= 1e-6 or t >= 1 - 1e-6:
            return None
        if dist((a[0] + t * dx, a[1] + t * dy), v) > PERP:
            return None
        return t

    polys = []
    for f in group:
        fid = hz.eid(f.Id)
        for lp in floor_all_loops(doc, f):
            if sarea(lp) < 0:
                lp = lp[::-1]
            polys.append((fid, lp))
    if not polys:
        return None

    all_v = []
    for fid, poly in polys:
        all_v.extend(poly)

    edges = []
    for (fid, poly) in polys:
        n = len(poly)
        for i in range(n):
            a = poly[i]
            b = poly[(i + 1) % n]
            ts = set([0.0, 1.0])
            for v in all_v:
                t = param_on(a, b, v)
                if t is not None:
                    ts.add(t)
            ts = sorted(ts)
            for k in range(len(ts) - 1):
                p = (a[0] + (b[0] - a[0]) * ts[k], a[1] + (b[1] - a[1]) * ts[k])
                q = (a[0] + (b[0] - a[0]) * ts[k + 1], a[1] + (b[1] - a[1]) * ts[k + 1])
                if dist(p, q) > 1e-6:
                    edges.append((fid, p, q))

    bucket = {}
    for (fid, p, q) in edges:
        bucket.setdefault(frozenset([rk(p), rk(q)]), []).append(fid)

    keep = []
    for (fid, p, q) in edges:
        if any(x != fid for x in bucket[frozenset([rk(p), rk(q)])]):
            continue
        keep.append((p, q))

    startidx = {}
    for idx, (p, q) in enumerate(keep):
        startidx.setdefault(rk(p), []).append(idx)

    used = set()
    loops = []
    for start in range(len(keep)):
        if start in used:
            continue
        loop = []
        cur = start
        guard = 0
        while cur is not None and guard < 200000:
            guard += 1
            if cur in used:
                break
            used.add(cur)
            p, q = keep[cur]
            loop.append(p)
            cands = [i for i in startidx.get(rk(q), []) if i not in used]
            cur = cands[0] if cands else None
        if len(loop) >= 3:
            loops.append(loop)

    if not loops:
        return None
    loops.sort(key=lambda L: abs(sarea(L)), reverse=True)
    ml = loops[0]
    if sarea(ml) < 0:
        ml = ml[::-1]
    return ml


def _line_int(p1, d1, p2, d2):
    den = d1[0] * d2[1] - d1[1] * d2[0]
    if abs(den) < 1e-9:
        return None
    t = ((p2[0] - p1[0]) * d2[1] - (p2[1] - p1[1]) * d2[0]) / den
    return (p1[0] + d1[0] * t, p1[1] + d1[1] * t)


def offset_polygon(P, d):
    """Mitred offset of a closed polygon. d>0 outward for a CCW ring."""
    n = len(P)
    out = []
    for i in range(n):
        a = P[(i - 1) % n]
        b = P[i]
        c = P[(i + 1) % n]
        dax, day = b[0] - a[0], b[1] - a[1]
        La = math.hypot(dax, day)
        dcx, dcy = c[0] - b[0], c[1] - b[1]
        Lc = math.hypot(dcx, dcy)
        if La < 1e-9 or Lc < 1e-9:
            out.append((b[0], b[1]))
            continue
        nax, nay = day / La, -dax / La
        ncx, ncy = dcy / Lc, -dcx / Lc
        p1 = (a[0] + nax * d, a[1] + nay * d)
        p2 = (b[0] + ncx * d, b[1] + ncy * d)
        inter = _line_int(p1, (dax, day), p2, (dcx, dcy))
        if inter is None or dist(inter, b) > abs(d) * MITER_LIMIT:
            out.append((b[0] + ((nax + ncx) / 2) * d, b[1] + ((nay + ncy) / 2) * d))
        else:
            out.append(inter)
    return out


def resample_with_corners(ml, step):
    """Exact outline vertices (the corners) plus sub-points every `step` along the
    long edges, so mitred corners survive."""
    E = []
    m = len(ml)
    for i in range(m):
        a = ml[i]
        b = ml[(i + 1) % m]
        seg = dist(a, b)
        E.append(a)
        if seg > step:
            ux, uy = (b[0] - a[0]) / seg, (b[1] - a[1]) / seg
            s = 1
            while s * step < seg - 0.15:
                E.append((a[0] + ux * s * step, a[1] + uy * s * step))
                s += 1
    Ef = []
    for p in E:
        if not Ef or dist(Ef[-1], p) > 0.10:
            Ef.append(p)
    if len(Ef) > 1 and dist(Ef[0], Ef[-1]) < 0.10:
        Ef = Ef[:-1]
    return Ef


def _pip(px, py, poly):
    ins = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > py) != (y2 > py):
            den = (y2 - y1) if abs(y2 - y1) > 1e-12 else 1e-12
            xi = x1 + (py - y1) * (x2 - x1) / den
            if xi >= px:
                ins = not ins
    return ins


def _dmin(px, py, L):
    best = 1e18
    n = len(L)
    for i in range(n):
        ax, ay = L[i]
        bx, by = L[(i + 1) % n]
        abx, aby = bx - ax, by - ay
        L2 = abx * abx + aby * aby
        t = 0.0
        if L2 > 1e-12:
            t = max(0.0, min(1.0, ((px - ax) * abx + (py - ay) * aby) / L2))
        best = min(best, dist((px, py), (ax + t * abx, ay + t * aby)))
    return best


# --------------------------------------------------------------- slab Z

def group_thickness(doc, group):
    th = 0.0
    for f in group:
        try:
            comp = doc.GetElement(f.GetTypeId()).GetCompoundStructure()
            if comp:
                th = max(th, comp.GetWidth())
        except Exception:
            pass
    return th


def _big_loop(doc, floor):
    best = None
    ba = -1.0
    for lp in floor_all_loops(doc, floor):
        a = abs(sarea(lp))
        if a > ba:
            ba = a
            best = lp
    return best


def group_top_sampler(doc, group):
    """z(x, y) for the group's TOP face - flat or sloped, using each slab's plane
    equation. Falls back to a constant bbox elevation for a slab with no usable
    planar top."""
    planes = {}
    loops = {}
    fallback = []
    for f in group:
        fid = hz.eid(f.Id)
        loops[fid] = _big_loop(doc, f)
        planes[fid] = _floor_plane(f)
        if planes[fid] is None:
            bb = f.get_BoundingBox(None)
            fallback.append(bb.Max.Z if bb else 0.0)
    const_z = max(fallback) if fallback else 0.0
    fids = [hz.eid(f.Id) for f in group]

    def z_at(x, y):
        sel = None
        for fid in fids:
            lp = loops.get(fid)
            if lp and _pip(x, y, lp):
                sel = fid
                break
        if sel is None:
            best = 1e18
            for fid in fids:
                lp = loops.get(fid)
                if not lp:
                    continue
                d = _dmin(x, y, lp)
                if d < best:
                    best = d
                    sel = fid
        pl = planes.get(sel) if sel is not None else None
        if pl is None:
            return const_z
        nx, ny, nz, ox, oy, oz = pl
        return oz - (nx * (x - ox) + ny * (y - oy)) / nz

    return z_at


def build_group_rings(doc, group, offset, spacing):
    ml = merged_boundary(doc, group)
    if ml is None or len(ml) < 3:
        return None
    inner = cm(DEFAULT_INNER_CM)
    thick = group_thickness(doc, group)
    z_at = group_top_sampler(doc, group)

    Ef = resample_with_corners(ml, spacing)
    off = offset_polygon(Ef, offset)
    inn = offset_polygon(Ef, -inner)

    zc = [z_at(p[0], p[1]) for p in Ef]
    edge_pts = [(Ef[i][0], Ef[i][1], zc[i]) for i in range(len(Ef))]
    off_pts = [(off[i][0], off[i][1], zc[i]) for i in range(len(Ef))]
    inn_pts = [(inn[i][0], inn[i][1], zc[i] - thick) for i in range(len(Ef))]
    return {
        "ml": ml,
        "edge": edge_pts,
        "offset": off_pts,
        "inner": inn_pts,
        "floor_ids": [hz.eid(f.Id) for f in group],
    }


# --------------------------------------------------------------- the toposolid

def _resolve_toposolid(doc, args):
    """Explicit id, or the only one in the document. Ambiguity is REFUSED with the
    candidates named - the button clicked its way out of this, and guessing which
    terrain to reshape is not a decision to make on the caller's behalf."""
    topo_id = hz.arg(args, "toposolid_id")
    if topo_id:
        topo = doc.GetElement(hz.to_eid(topo_id))
        if not isinstance(topo, Toposolid):
            raise Exception("toposolid_id {0} is not a Toposolid.".format(topo_id))
        return topo, "named by the caller"

    all_topos = list(FilteredElementCollector(doc).OfClass(Toposolid)
                     .WhereElementIsNotElementType().ToElements())
    if not all_topos:
        raise Exception("This document contains no Toposolid, so there is no terrain to reshape.")
    if len(all_topos) > 1:
        raise Exception(
            "This document contains {0} Toposolids and toposolid_id was not given: {1}. Name the one "
            "you mean - reshaping the wrong terrain is not something to resolve by guessing.".format(
                len(all_topos), ", ".join(str(hz.eid(t.Id)) for t in all_topos)))
    return all_topos[0], "the only Toposolid in the document"


def _vertex_keys(editor, dtol):
    """Existing vertex positions as rounded keys. Reads SlabShapeVertices ONLY -
    iterating SlabShapeCreases crashes Revit on large toposolids."""
    keys = {}
    for v in editor.SlabShapeVertices:
        p = v.Position
        keys[(int(round(p.X / dtol)), int(round(p.Y / dtol)), int(round(p.Z / dtol)))] = v
    return keys


def _editor(topo):
    ed = topo.GetSlabShapeEditor()
    if ed is None:
        raise Exception("This Toposolid exposes no SlabShapeEditor; its shape cannot be edited.")
    if not ed.IsEnabled:
        ed.Enable()
    return ed


# ---- the host contract: plan / apply / verify ------------------------------

def _params(args):
    offset_cm = float(hz.arg(args, "offset_cm", 5.0))
    spacing_cm = float(hz.arg(args, "spacing_cm", 100.0))
    if offset_cm <= 0 or spacing_cm <= 0:
        raise Exception("offset_cm and spacing_cm must both be greater than zero.")
    return cm(offset_cm), cm(spacing_cm)


def _build(doc, args):
    """Everything both plan() and apply() need. Recomputed rather than carried
    between them: the model can move in between, and stale geometry is the one
    thing a plan must not hand to an apply."""
    offset, spacing = _params(args)
    topo, how = _resolve_toposolid(doc, args)

    scope = hz.resolve(doc, args, lambda e: isinstance(e, Floor), of_class=Floor)
    if not scope.elements:
        raise Exception("No floor resolved, so there is nothing to embed.")

    groups = group_floors(doc, scope.elements)

    data = []
    failed = []
    for g in groups:
        try:
            gd = build_group_rings(doc, g, offset, spacing)
        except Exception as exc:
            failed.append({"floor_ids": [hz.eid(f.Id) for f in g], "error": hz.brief(exc)})
            continue
        if gd is None:
            failed.append({"floor_ids": [hz.eid(f.Id) for f in g],
                           "error": "no merged outline could be traced from these slabs"})
            continue
        data.append(gd)

    return topo, how, scope, data, failed


def plan(doc, args):
    topo, how, scope, data, failed = _build(doc, args)

    groups = []
    for gd in data:
        groups.append({
            "floor_ids": gd["floor_ids"],
            "floors": len(gd["floor_ids"]),
            "outline_vertices": len(gd["ml"]),
            "ring_points": len(gd["edge"]),
            "would_add_points": len(gd["edge"]) + len(gd["offset"]) + len(gd["inner"]),
            "would_add_split_lines": len(gd["offset"]) + len(gd["inner"]),
        })

    return {
        "scope": scope.report(),
        "toposolid_id": hz.eid(topo.Id),
        "toposolid_resolved_by": how,
        "offset_cm": float(hz.arg(args, "offset_cm", 5.0)),
        "spacing_cm": float(hz.arg(args, "spacing_cm", 100.0)),
        "groups": groups,
        "groups_failed": failed,
        "would_process_groups": len(groups),
        "would_add_points": sum(g["would_add_points"] for g in groups),
        "would_add_split_lines": sum(g["would_add_split_lines"] for g in groups),
        "note": ("Slabs that touch AND are level where they touch are merged into one outline, so no "
                 "split line is drawn along a false seam. Slabs with a real STEP between them are kept "
                 "apart on purpose - that step is a design feature, not a seam. Existing toposolid "
                 "points within {0}cm of each outline are DELETED first, to stop the triangulation "
                 "banding.".format(CLEAN_NEAR_CM)),
    }


def apply(doc, args, plan):
    topo, how, scope, data, failed = _build(doc, args)
    if not data:
        raise Exception("No outline could be built at apply time; nothing was written.")

    near = cm(CLEAN_NEAR_CM)
    dtol = mm(DUP_TOL_MM)
    ed = _editor(topo)

    deleted = 0
    added = 0
    splits = 0

    # 1. Clear stale points in and around each outline.
    for gd in data:
        ml = gd["ml"]
        xs = [p[0] for p in ml]
        ys = [p[1] for p in ml]
        minx, maxx = min(xs) - 1, max(xs) + 1
        miny, maxy = min(ys) - 1, max(ys) + 1
        todel = []
        for v in ed.SlabShapeVertices:
            p = v.Position
            if not (minx < p.X < maxx and miny < p.Y < maxy):
                continue
            if _dmin(p.X, p.Y, ml) <= near or _pip(p.X, p.Y, ml):
                todel.append(v)
        for v in todel:
            try:
                if ed.DeletePoint(v):
                    deleted += 1
            except Exception:
                pass

    doc.Regenerate()

    # 2. Existing vertices by position, so nothing is added twice.
    vk = _vertex_keys(ed, dtol)

    def addpt(p):
        k = (int(round(p[0] / dtol)), int(round(p[1] / dtol)), int(round(p[2] / dtol)))
        if k in vk:
            return vk[k]
        try:
            vv = ed.AddPoint(XYZ(p[0], p[1], p[2]))
            if vv:
                vk[k] = vv
                return vv
        except Exception:
            pass
        return None

    # 3. Points and split lines, per group.
    for gd in data:
        ov = [addpt(p) for p in gd["offset"]]
        iv = [addpt(p) for p in gd["inner"]]
        for p in gd["edge"]:
            addpt(p)
        added += sum(1 for v in ov if v) + sum(1 for v in iv if v)
        for ring in (ov, iv):
            m = len(ring)
            for i in range(m):
                a = ring[i]
                b = ring[(i + 1) % m]
                if a is None or b is None:
                    continue
                try:
                    if ed.AddSplitLine(a, b):
                        splits += 1
                except Exception:
                    pass

    # The DISTINCT positions this run should have left behind. Deduplicated with the
    # same key the adder uses, so it is comparable to what verify() finds: two rings
    # that coincide at a corner are one vertex, and counting them as two would
    # manufacture a mismatch out of arithmetic rather than out of the model.
    expected = set()
    for gd in data:
        for p in gd["edge"] + gd["offset"] + gd["inner"]:
            expected.add((int(round(p[0] / dtol)), int(round(p[1] / dtol)), int(round(p[2] / dtol))))

    return {
        "toposolid_id": hz.eid(topo.Id),
        "groups_processed": len(data),
        "groups_failed": failed,
        "points_deleted": deleted,
        "points_added": added,
        "split_lines_added": splits,
        "points_expected": len(expected),
    }


def verify(doc, args, plan, applied):
    """After the commit, RECOMPUTE where the points should be and ask the model
    whether a vertex is really there. An independent re-read, not a tally of
    AddPoint calls that did not throw.

    Counts vertices only. Iterating SlabShapeCreases crashes Revit on large
    toposolids, and a verification that can take the host down is not one."""
    topo, how, scope, data, failed = _build(doc, args)
    dtol = mm(DUP_TOL_MM)

    ed = topo.GetSlabShapeEditor()
    if ed is None:
        return {"points_present": 0, "intended_points": applied["points_expected"],
                "note": "the Toposolid no longer exposes a shape editor"}

    actual = []
    for v in ed.SlabShapeVertices:
        p = v.Position
        actual.append((p.X, p.Y, p.Z))

    # Deduplicated the same way apply() keys them, so the two counts are comparable.
    seen = set()
    wanted = []
    for gd in data:
        for p in gd["edge"] + gd["offset"] + gd["inner"]:
            k = (int(round(p[0] / dtol)), int(round(p[1] / dtol)), int(round(p[2] / dtol)))
            if k not in seen:
                seen.add(k)
                wanted.append(p)

    # Revit SNAPS a requested point onto existing geometry - a slab edge - by up to
    # about 14mm, measured on this Revit. Matching by rounded key alone called
    # those points missing while they sat a centimetre away at the same elevation.
    # See hz.match_positions for why the tolerance is derived rather than chosen.
    exact, near, tol = hz.match_positions(wanted, actual, ceiling=mm(25.0))

    return {
        "points_present": exact + near,
        "points_exact": exact,
        "points_within_tolerance": near,
        "match_tolerance_mm": round(tol / mm(1.0), 2),
        "points_recomputed": len(wanted),
        "intended_points": applied["points_expected"],
        "toposolid_vertices_now": len(actual),
        "note": (None if near == 0 else
                 "{0} of the {1} points are not at the exact position asked for but within "
                 "{2:.1f}mm of it - Revit snaps a point that lands on existing geometry. They are "
                 "reported apart from the exact ones rather than folded in.".format(
                     near, exact + near, tol / mm(1.0))),
    }
