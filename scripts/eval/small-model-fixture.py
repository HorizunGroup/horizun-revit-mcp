#!/usr/bin/env python3
"""
Build the disposable model the small-model battery runs against, and record what
it contains so every task can be checked against known values.

The model is created from Revit's default project template at --path (which must
not exist), populated through typed writes only (rehearse, then apply with the
token), saved, and described in --out (fixture.json): level, wall, door, floor and
room ids plus the expected answers. Nothing here touches a model you did not
create: --path must be new, and every write names that document.

    python scripts/eval/small-model-fixture.py --server <horizun-mcp.exe> \
        --path C:/scratch/fixture.rvt --out C:/scratch/fixture.json

Environment the server needs (HORIZUN_DATA_ROOT, HORIZUN_REVIT_YEAR) is passed
through from --data-root / --revit-year.
"""
import argparse
import json
import os
import sys
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_stdio import McpServer  # noqa: E402


def apply(server, tool, args):
    """Rehearse, then apply with the token. Fails loudly on any refusal."""
    err, dry, _ = server.call(tool, dict(args))
    if err or not isinstance(dry, dict) or not dry.get("confirmation_token"):
        raise RuntimeError(f"{tool} rehearsal refused: {json.dumps(dry, ensure_ascii=False)[:1500]}")
    final = dict(args, dry_run=False, confirmation_token=dry["confirmation_token"],
                 idempotency_key="fixture-" + uuid.uuid4().hex)
    err, done, _ = server.call(tool, final)
    if err or not isinstance(done, dict):
        raise RuntimeError(f"{tool} apply failed: {json.dumps(done, ensure_ascii=False)[:1500]}")
    return done


def rows(server, args):
    err, reply, _ = server.call("horizun_query_model", dict(args, response_mode="compact", max_rows=500))
    if err:
        raise RuntimeError(json.dumps(reply, ensure_ascii=False)[:1500])
    return reply.get("rows") or []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", required=True)
    ap.add_argument("--path", required=True, help="new .rvt to create; must not exist")
    ap.add_argument("--out", required=True)
    ap.add_argument("--data-root")
    ap.add_argument("--revit-year", default="2024")
    ap.add_argument("--template", help="project template; the battery runs on an English Revit with "
                                       "English\\Construction-DefaultMetric.rte")
    ap.add_argument("--existing", action="store_true",
                    help="--path was created by an earlier run of this script and is the ACTIVE, still empty document")
    a = ap.parse_args()
    if os.path.exists(a.path) and not a.existing:
        raise SystemExit("--path already exists; the fixture is only ever built into a new file.")
    env = {"HORIZUN_REVIT_YEAR": a.revit_year}
    if a.data_root:
        env["HORIZUN_DATA_ROOT"] = a.data_root
    s = McpServer(a.server, env)
    try:
        if a.existing:
            _, info, _ = s.call("get_document_info", {})
            if os.path.normcase(os.path.abspath(info.get("path") or "")) != os.path.normcase(os.path.abspath(a.path)):
                raise SystemExit("--existing: the active document is not --path; nothing was written.")
            title = info["title"]
        else:
            create = {"operation": "new_project", "save_as_path": a.path}
            if a.template:
                create["template_path"] = a.template
            doc = apply(s, "horizun_document_session", create)
            title = doc["title"]
        levels = sorted((r for r in rows(s, {"categories": ["OST_Levels"]})), key=lambda r: r["element_id"])
        lv1, lv2 = levels[0]["element_id"], levels[1]["element_id"]
        types = rows(s, {"categories": ["OST_Doors", "OST_Floors"], "include_types": True,
                         "return_fields": ["name", "type_id", "is_element_type", "category"]})
        door_type = next(r["type_id"] for r in types if r.get("is_element_type") and "915" in (r.get("name") or ""))
        floor_type = next(r["type_id"] for r in types if r.get("is_element_type") and "150" in (r.get("name") or "")
                          and not any(w in (r.get("name") or "").lower() for w in ("fill", "rellen")))
        # Level 1: a closed 10 x 8 m rectangle, 3 m high. Level 2: two loose walls.
        walls_l1 = [((0, 0), (10000, 0)), ((10000, 0), (10000, 8000)), ((10000, 8000), (0, 8000)), ((0, 8000), (0, 0))]
        walls_l2 = [((0, 0), (6000, 0)), ((0, 0), (0, 5000))]
        # Wall endpoints carry ABSOLUTE Z (internal origin), not an offset from level_id:
        # a Z of 0 on Level 2 builds the wall at Level 1's height with a negative base offset.
        _, lv_rows, _ = s.call("horizun_query_model", {"element_ids": [lv2], "return_parameters": ["LEVEL_ELEV"],
                                                       "parameter_format": "compact"})
        l2_elev = lv_rows["rows"][0]["parameters"]["LEVEL_ELEV"]
        elements = [{"kind": "wall", "level_id": lv1, "start": [x0, y0, 0], "end": [x1, y1, 0], "height": 3000}
                    for (x0, y0), (x1, y1) in walls_l1]
        elements += [{"kind": "wall", "level_id": lv2, "start": [x0, y0, l2_elev], "end": [x1, y1, l2_elev], "height": 3000}
                     for (x0, y0), (x1, y1) in walls_l2]
        elements.append({"kind": "floor", "level_id": lv1, "type_id": floor_type,
                         "profile": [[[0, 0, 0], [10000, 0, 0], [10000, 8000, 0], [0, 8000, 0]]]})
        # Each stage runs only if its content is absent, so an interrupted build resumes
        # on the same document; ids are always read back from the model, never assumed.
        def ids_of(category, level=None):
            q = {"categories": [category], "return_fields": ["name", "level"]}
            return sorted(r["element_id"] for r in rows(s, q) if level is None or r.get("level") == level)

        if not ids_of("OST_Walls"):
            apply(s, "horizun_create_elements", {"target_document": title, "units": "mm", "elements": elements})
        wall_ids = ids_of("OST_Walls", levels[0]["name"]) + ids_of("OST_Walls", levels[1]["name"])
        floor_ids = ids_of("OST_Floors")
        if len(wall_ids) != 6 or len(floor_ids) != 1:
            raise SystemExit(f"unexpected content: walls={wall_ids} floors={floor_ids}; nothing more was written.")
        floor_id = floor_ids[0]
        doors = [{"kind": "family_instance", "type_id": door_type, "level_id": lv1, "host_id": wall_ids[0],
                  "point": [3000, 0, 0], "coordinate_mode": "level_offset"},
                 {"kind": "family_instance", "type_id": door_type, "level_id": lv1, "host_id": wall_ids[2],
                  "point": [7000, 8000, 0], "coordinate_mode": "level_offset"},
                 {"kind": "room", "level_id": lv1, "point": [5000, 4000], "name": "Sala de prueba"}]
        if not ids_of("OST_Doors"):
            apply(s, "horizun_create_elements", {"target_document": title, "units": "mm", "elements": doors})
        ids2 = ids_of("OST_Doors") + ids_of("OST_Rooms")
        wall_type = rows(s, {"element_ids": wall_ids[:1], "return_fields": ["type"]})[0].get("type")
        room = rows(s, {"element_ids": ids2[2:3], "return_parameters": ["ROOM_AREA"], "parameter_format": "compact"})
        room_area = round(room[0]["parameters"]["ROOM_AREA"], 2) if room else None
        err, saved, _ = s.call("horizun_document_session", {"operation": "save", "target_document": title,
                                                             "idempotency_key": "fixture-save-" + uuid.uuid4().hex})
        if err:
            raise RuntimeError("save failed: " + json.dumps(saved, ensure_ascii=False)[:800])
        fixture = {
            "schema": "horizun.small-model-fixture/1",
            "document": title, "path": a.path,
            "levels": {"level_1": {"id": lv1, "name": levels[0].get("name")},
                       "level_2": {"id": lv2, "name": levels[1].get("name"), "elevation_mm": l2_elev}},
            "wall_ids_level_1": wall_ids[:4], "wall_ids_level_2": wall_ids[4:6],
            "floor_id": floor_id, "door_ids": ids2[:2], "room_id": ids2[2] if len(ids2) > 2 else None,
            "wall_type": wall_type,
            "expected": {"walls": 6, "walls_level_1": 4, "walls_level_2": 2, "doors": 2, "floors": 1, "rooms": 1,
                         # Location-line length; the room is measured inside 138 mm partitions.
                         "wall_length_total_m": 47.0, "floor_area_m2": 80.0, "room_area_m2": room_area,
                         "levels": len(levels)},
        }
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(fixture, f, ensure_ascii=False, indent=2)
        print(json.dumps(fixture, ensure_ascii=False, indent=2))
    finally:
        s.close()


if __name__ == "__main__":
    main()
