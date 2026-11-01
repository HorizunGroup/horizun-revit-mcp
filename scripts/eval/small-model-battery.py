#!/usr/bin/env python3
"""
Horizun Revit MCP - small-model battery.

Runs the tasks in small-model-tasks.json against a live, DISPOSABLE fixture
(small-model-fixture.py) through one of two agents and records, per run: whether
the task succeeded (checked against the model, not the agent's word), whether the
FIRST Horizun tool call was a right one, how many calls it took, and tokens.

  agent=ollama  a minimal tool loop over Ollama's /api/chat (what an Ollama or
                LM Studio user gets: the server's tools, no other system prompt);
  agent=claude  `claude -p --model <m>` with only this MCP server (strict config).

    python scripts/eval/small-model-battery.py --agent ollama --model qwen3:8b \
        --config local --server <dev horizun-mcp.exe> --fixture fixture.json \
        --pristine fixture-pristine.rvt --out results/

Configs: local    HORIZUN_TOOLSETS=local
         facade   HORIZUN_SCHEMA_MODE=facade
         compact  HORIZUN_TOOLSETS=local + HORIZUN_SCHEMA_MODE=compact
The bridge state root (HORIZUN_DATA_ROOT) and Revit year come from --data-root and
--revit-year and are passed to every server process, so a run never reads or writes
another installation's state.
"""
import argparse
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unicodedata
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from mcp_stdio import McpServer  # noqa: E402

CONFIGS = {
    "local": {"HORIZUN_TOOLSETS": "local"},
    "facade": {"HORIZUN_SCHEMA_MODE": "facade"},
    "compact": {"HORIZUN_TOOLSETS": "local", "HORIZUN_SCHEMA_MODE": "compact"},
}
META_TOOLS = {"horizun_list_tools", "horizun_describe_tool", "horizun_activate_toolset"}
SYSTEM_PROMPT = ("You operate Autodesk Revit through the Horizun tools. Use the tools to answer; "
                 "do not invent values. Answer in the user's language, briefly.")


def fold(text):
    text = unicodedata.normalize("NFKD", text or "")
    return "".join(c for c in text if not unicodedata.combining(c)).lower()


def fill(value, fx):
    """Replace {placeholders} from the fixture in strings, lists and dicts."""
    if isinstance(value, str):
        out = value
        for k, v in fx.items():
            out = out.replace("{" + k + "}", str(v))
        if value.startswith("{") and value.endswith("}") and re.fullmatch(r"\d+(\.\d+)?", out):
            return float(out) if "." in out else int(out)
        return out
    if isinstance(value, list):
        return [fill(v, fx) for v in value]
    if isinstance(value, dict):
        return {k: fill(v, fx) for k, v in value.items()}
    return value


def placeholders(fixture):
    walls = fixture["wall_ids_level_1"] + fixture["wall_ids_level_2"]
    p = {"document": fixture["document"], "level_1": fixture["levels"]["level_1"]["name"],
         "level_2": fixture["levels"]["level_2"]["name"], "wall_type": fixture["wall_type"],
         "room_area_m2": fixture["expected"]["room_area_m2"]}
    for i, w in enumerate(walls):
        p[f"wall_{i}"] = w
    for i, d in enumerate(fixture["door_ids"]):
        p[f"door_{i}"] = d
    return p


# ---- the bridge used by the harness itself (reset and state checks) ----------------

class Bridge:
    def __init__(self, server, env, fixture, pristine):
        self.server, self.env, self.fx, self.pristine = server, env, fixture, pristine

    def _open(self):
        return McpServer(self.server, self.env)

    def query(self, args):
        s = self._open()
        try:
            err, r, _ = s.call("horizun_query_model", dict(args, response_mode="compact", max_rows=500))
            if err:
                raise RuntimeError(json.dumps(r, ensure_ascii=False)[:800])
            return r.get("rows") or []
        finally:
            s.close()

    def reset(self):
        """Close the working fixture discarding changes, restore the pristine bytes, reopen it."""
        s = self._open()
        try:
            path, title = self.fx["path"], self.fx["document"]
            def session(op, what):
                err, dry, _ = s.call("horizun_document_session", dict(op, dry_run=True, idempotency_key=uuid.uuid4().hex))
                final = dict(op, dry_run=False, idempotency_key=uuid.uuid4().hex)
                if not err and isinstance(dry, dict) and dry.get("confirmation_token"):
                    final["confirmation_token"] = dry["confirmation_token"]
                err, done, _ = s.call("horizun_document_session", final)
                if err:
                    raise RuntimeError(f"reset {what} failed: " + json.dumps(done, ensure_ascii=False)[:800])

            _, info, _ = s.call("get_document_info", {})
            if isinstance(info, dict) and info.get("title") == title:
                session({"operation": "close", "target_document": title, "discard_unsaved": True,
                         "activate_other": True}, "close")
            # Revit releases the file a moment after Close() returns.
            for attempt in range(60):
                try:
                    shutil.copyfile(self.pristine, path)
                    break
                except OSError:
                    if attempt == 59:
                        raise
                    time.sleep(1)
            session({"operation": "open", "file_path": path,
                     "expected_version": self.env.get("HORIZUN_REVIT_YEAR", "2024")}, "open")
            _, info, _ = s.call("get_document_info", {})
            if not isinstance(info, dict) or info.get("title") != title:
                raise RuntimeError("reset: fixture is not the active document after reopening")
        finally:
            s.close()


# ---- agents --------------------------------------------------------------------------

def ollama_tools(tools):
    return [{"type": "function", "function": {"name": t["name"], "description": t.get("description") or "",
                                               "parameters": t.get("inputSchema") or {"type": "object"}}}
            for t in tools]


def run_ollama(model, prompt, server, env, args):
    s = McpServer(server, env)
    calls, tokens_in, tokens_out = [], 0, 0
    answer, state = "", "completed"
    try:
        tools = s.tools()
        system = SYSTEM_PROMPT
        instr = (s.initialize or {}).get("instructions")
        if instr and args.include_instructions:
            system += "\n\n" + instr
        messages = [{"role": "system", "content": system}, {"role": "user", "content": prompt}]
        started = time.time()
        for _ in range(args.max_turns):
            body = {"model": model, "messages": messages, "tools": ollama_tools(tools), "stream": False,
                    "think": args.think, "options": {"num_ctx": args.num_ctx, "temperature": 0}}
            req = urllib.request.Request(args.ollama_url.rstrip("/") + "/api/chat", data=json.dumps(body).encode(),
                                         headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=args.timeout) as r:
                resp = json.loads(r.read().decode("utf-8"))
            tokens_in += resp.get("prompt_eval_count") or 0
            tokens_out += resp.get("eval_count") or 0
            msg = resp.get("message") or {}
            messages.append(msg)
            tcs = msg.get("tool_calls") or []
            if not tcs:
                answer = msg.get("content") or ""
                break
            for tc in tcs:
                fn = tc.get("function") or {}
                name, arguments = fn.get("name"), fn.get("arguments") or {}
                if isinstance(arguments, str):
                    try:
                        arguments = json.loads(arguments)
                    except ValueError:
                        arguments = {}
                try:
                    is_err, payload, text = s.call(name, arguments)
                except Exception as e:  # unknown tool, transport error
                    is_err, payload, text = True, str(e), str(e)
                calls.append({"name": name, "input": arguments, "is_error": is_err,
                              "payload": payload if isinstance(payload, dict) else None})
                messages.append({"role": "tool", "tool_name": name, "content": text[:args.max_tool_chars]})
            if time.time() - started > args.timeout:
                state = "timeout"
                break
        else:
            state = "max_turns"
    except Exception as e:
        state, answer = "crashed", f"{type(e).__name__}: {e}"
    finally:
        s.close()
    return {"state": state, "answer": answer, "calls": calls, "tokens_in": tokens_in, "tokens_out": tokens_out}


def run_claude(model, prompt, server, env, args):
    tmp = tempfile.mkdtemp(prefix="hz-small-claude-")
    cfg = os.path.join(tmp, "mcp.json")
    with open(cfg, "w", encoding="utf-8") as f:
        json.dump({"mcpServers": {"horizun-revit": {"command": server, "args": [], "env": env}}}, f)
    cmd = [args.claude, "-p", prompt, "--model", model, "--mcp-config", cfg, "--strict-mcp-config",
           "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--tools", "",
           "--allowedTools", "mcp__horizun-revit__*", "--permission-mode", "dontAsk",
           "--disable-slash-commands", "--setting-sources", "", "--no-chrome"]
    calls, answer, state, usage = {}, "", "completed", {}
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", cwd=tmp, timeout=args.timeout)
        order = []
        for line in p.stdout.splitlines():
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if e.get("type") == "assistant":
                for b in e["message"].get("content", []):
                    if b.get("type") == "tool_use":
                        calls[b["id"]] = {"name": b["name"].split("__")[-1], "input": b.get("input"),
                                          "is_error": None, "payload": None}
                        order.append(b["id"])
            elif e.get("type") == "user":
                for b in e["message"].get("content", []):
                    if b.get("type") == "tool_result" and b.get("tool_use_id") in calls:
                        c = calls[b["tool_use_id"]]
                        c["is_error"] = bool(b.get("is_error"))
                        raw = b.get("content")
                        text = raw if isinstance(raw, str) else "".join(x.get("text", "") for x in raw or [] if x.get("type") == "text")
                        try:
                            c["payload"] = json.loads(text)
                        except ValueError:
                            pass
            elif e.get("type") == "result":
                answer = e.get("result") or ""
                usage = e.get("usage") or {}
                if e.get("is_error"):
                    state = "crashed"
        calls = [calls[i] for i in order]
    except subprocess.TimeoutExpired:
        state, calls = "timeout", list(calls.values())
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    tin = sum(usage.get(k) or 0 for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"))
    return {"state": state, "answer": answer, "calls": calls, "tokens_in": tin,
            "tokens_out": usage.get("output_tokens") or 0}


# ---- scoring ------------------------------------------------------------------------

def effective(call):
    """horizun_call_tool counts as the tool it dispatches."""
    if call["name"] == "horizun_call_tool":
        inner = (call.get("input") or {}).get("name")
        return inner or call["name"], (call.get("input") or {}).get("arguments") or {}
    return call["name"], call.get("input") or {}


def applied(call):
    name, inp = effective(call)
    return not call.get("is_error") and inp.get("dry_run") is False


def numbers_in(text):
    return [float(n.replace(",", ".")) for n in re.findall(r"\d+(?:[.,]\d+)?", text or "")]


def check(task, run, bridge, fx_ids):
    c = task["check"]
    ans = fold(run["answer"])
    eff = [(effective(x)[0], x) for x in run["calls"]]
    ok = True
    if "answer_number" in c:
        tol = c.get("tolerance", 0)
        ok &= any(abs(n - c["answer_number"]) <= tol for n in numbers_in(run["answer"]))
    if "answer_contains_all" in c:
        ok &= all(fold(str(s)) in ans for s in c["answer_contains_all"])
    if "answer_contains_any" in c:
        ok &= any(fold(s) in ans for s in c["answer_contains_any"])
    if "tool_succeeded" in c:
        ok &= any(n in c["tool_succeeded"] and not x.get("is_error") for n, x in eff)
    if "tool_applied" in c:
        ok &= all(any(n == t and applied(x) for n, x in eff) for t in c["tool_applied"])
    if "no_success_of" in c:
        ok &= not any(n in c["no_success_of"] and not x.get("is_error") for n, x in eff)
    if "tool_succeeded_or_answer_any" in c:
        sub = c["tool_succeeded_or_answer_any"]
        ok &= (any(n in sub["tools"] and not x.get("is_error") for n, x in eff)
               or any(fold(s) in ans for s in sub["answer"]))
    if any(k.startswith("state_") for k in c):
        walls = bridge.query({"categories": ["OST_Walls"], "return_parameters": ["CURVE_ELEM_LENGTH"],
                              "parameter_format": "compact"})
        if "state_wall_count" in c:
            ok &= len(walls) == c["state_wall_count"]
        if "state_new_wall_length_m" in c:
            new = [w for w in walls if w["element_id"] not in fx_ids]
            ok &= len(new) == 1 and abs((new[0]["parameters"].get("CURVE_ELEM_LENGTH") or 0) / 1000.0
                                        - c["state_new_wall_length_m"]) < 0.01
        if "state_comments" in c:
            sc = c["state_comments"]
            rows = bridge.query({"element_ids": sc["ids"], "return_parameters": ["ALL_MODEL_INSTANCE_COMMENTS"],
                                 "parameter_format": "compact"})
            ok &= len(rows) == len(sc["ids"]) and all(
                fold(str(r["parameters"].get("ALL_MODEL_INSTANCE_COMMENTS") or "")) == fold(sc["value"]) for r in rows)
        if "state_moved_x_mm" in c:
            m = c["state_moved_x_mm"]
            rows = bridge.query({"element_ids": [m["id"]], "include_orientation": True})
            pt = (((rows or [{}])[0].get("placement") or {}).get("point") or [None])
            ok &= pt[0] is not None and abs(pt[0] - (m["origin_x"] + m["dx"])) <= m["tolerance"]
        if "state_absent" in c:
            ok &= not bridge.query({"element_ids": c["state_absent"]})
        if "state_level" in c:
            lv = c["state_level"]
            rows = bridge.query({"categories": ["OST_Levels"], "return_parameters": ["LEVEL_ELEV"],
                                 "parameter_format": "compact"})
            ok &= any(fold(r.get("name")) == fold(lv["name"]) and
                      abs((r["parameters"].get("LEVEL_ELEV") or -1e9) - lv["elevation_mm"]) <= lv["tolerance"] for r in rows)
        if "state_floor_count" in c:
            ok &= len(bridge.query({"categories": ["OST_Floors"]})) == c["state_floor_count"]
    return bool(ok)


def stopped_at_rehearsal(task, run):
    """A write task whose expected tool rehearsed successfully but was never applied: the
    agent handed the confirmation back instead of finishing. Safe, but not done."""
    if task["kind"] != "write":
        return False
    eff = [(effective(x)[0], effective(x)[1], x) for x in run["calls"]]
    rehearsed = any(n in task["expected_tools"] and not x.get("is_error") and inp.get("dry_run") is not False
                    for n, inp, x in eff)
    return rehearsed and not any(n in task["expected_tools"] and applied(x) for n, _, x in eff)


ORIENTATION = {"horizun_health", "horizun_target"}
READS = {"get_document_info", "horizun_list_elements", "horizun_query_model", "horizun_navigate"}


def first_call_right(task, calls, action=False):
    """The first call that does the task's work. Orientation (health/target), which the
    server instructions ask for first, is skipped unless the task itself is about it.
    action=True also skips the reads a careful agent makes before a WRITE task, so it
    judges the first tool that acts."""
    for name in calls:
        if name in META_TOOLS or name == "horizun_call_tool":
            continue
        if name in ORIENTATION and name not in task["expected_tools"]:
            continue
        if action and task["kind"] == "write" and name in READS:
            continue
        return name in task["expected_tools"]
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--agent", choices=["ollama", "claude"], required=True)
    ap.add_argument("--model", required=True)
    ap.add_argument("--config", choices=sorted(CONFIGS), required=True)
    ap.add_argument("--server", required=True)
    ap.add_argument("--fixture", required=True)
    ap.add_argument("--pristine", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--tasks", default=os.path.join(HERE, "small-model-tasks.json"))
    ap.add_argument("--only", nargs="*", help="task ids to run (default: all)")
    ap.add_argument("--label", default="", help="free text recorded with the run, e.g. before/after")
    ap.add_argument("--data-root")
    ap.add_argument("--revit-year", default="2024")
    ap.add_argument("--claude", default="claude")
    ap.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    ap.add_argument("--num-ctx", type=int, default=32768)
    ap.add_argument("--think", action="store_true")
    ap.add_argument("--include-instructions", action="store_true", default=True)
    ap.add_argument("--max-turns", type=int, default=10)
    ap.add_argument("--max-tool-chars", type=int, default=12000)
    ap.add_argument("--timeout", type=int, default=600)
    a = ap.parse_args()

    fixture = json.load(open(a.fixture, encoding="utf-8"))
    ph = placeholders(fixture)
    tasks = json.load(open(a.tasks, encoding="utf-8"))["tasks"]
    if a.only:
        tasks = [t for t in tasks if t["id"] in a.only]
    base_env = {"HORIZUN_REVIT_YEAR": a.revit_year}
    if a.data_root:
        base_env["HORIZUN_DATA_ROOT"] = a.data_root
    env = dict(base_env, **CONFIGS[a.config])
    bridge = Bridge(a.server, base_env, fixture, a.pristine)
    fx_ids = set(fixture["wall_ids_level_1"] + fixture["wall_ids_level_2"])
    bridge.reset()  # every run starts from the pristine fixture, active
    door0 =bridge.query({"element_ids": [fixture["door_ids"][0]], "include_orientation": True})
    door0_x = ((door0[0].get("placement") or {}).get("point") or [None])[0]

    os.makedirs(a.out, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_path = os.path.join(a.out, f"{a.agent}-{a.model.replace(':', '_')}-{a.config}-{stamp}.json")
    rows = []
    dirty = False
    for t in tasks:
        task = fill(t, ph)
        if "state_moved_x_mm" in task["check"]:
            task["check"]["state_moved_x_mm"]["origin_x"] = door0_x
        if task.get("reset") or dirty:
            bridge.reset()
        started = time.time()
        runner = run_ollama if a.agent == "ollama" else run_claude
        run = runner(a.model, task["prompt"], a.server, env, a)
        run["seconds"] = round(time.time() - started, 1)
        try:
            run["completed"] = run["state"] == "completed" and check(task, run, bridge, fx_ids)
        except Exception as e:
            run["completed"], run["check_error"] = False, str(e)
        names = [effective(x)[0] for x in run["calls"]]
        run["first_call_right"] = first_call_right(task, names)
        run["first_action_right"] = first_call_right(task, names, action=True)
        dirty = any(applied(x) for x in run["calls"])
        row = {"task": t["id"], "kind": t["kind"], "lang": t["lang"], **{k: run[k] for k in
               ("completed", "first_call_right", "first_action_right", "state", "seconds", "tokens_in", "tokens_out")},
               "tool_calls": len(run["calls"]), "calls": [effective(x)[0] for x in run["calls"]],
               "errors": sum(1 for x in run["calls"] if x.get("is_error")),
               "answer": run["answer"][:600], "check_error": run.get("check_error"),
               "stopped_at_rehearsal": stopped_at_rehearsal(task, run),
               "call_detail": [{"name": effective(x)[0], "input": effective(x)[1], "is_error": x.get("is_error"),
                                "error": (json.dumps(x.get("payload"), ensure_ascii=False)[:400]
                                          if x.get("is_error") else None)} for x in run["calls"]]}
        rows.append(row)
        print(f"{t['id']:24} ok={row['completed']!s:5} first={row['first_call_right']!s:5} calls={row['tool_calls']:2} "
              f"tok={row['tokens_in']:7}+{row['tokens_out']:5} {row['seconds']:6}s {row['state']}", flush=True)
        summary = {"schema": "horizun.small-model-battery/1", "agent": a.agent, "model": a.model,
                   "config": a.config, "label": a.label, "product": "Horizun Community",
                   "tasks": len(rows), "completed": sum(r["completed"] for r in rows),
                   "first_call_right": sum(r["first_call_right"] for r in rows),
                   "tokens_in": sum(r["tokens_in"] for r in rows), "tokens_out": sum(r["tokens_out"] for r in rows),
                   "tool_calls": sum(r["tool_calls"] for r in rows), "runs": rows}
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(summary, f, ensure_ascii=False, indent=2)
    if dirty:
        bridge.reset()
    print(f"\n{summary['completed']}/{len(rows)} completed, {summary['first_call_right']}/{len(rows)} first call right, "
          f"tokens {summary['tokens_in']}+{summary['tokens_out']} -> {out_path}")


if __name__ == "__main__":
    main()
