"""Minimal MCP stdio client for the evaluation scripts: one server process, sequential calls."""
import json
import os
import subprocess


class McpServer:
    def __init__(self, server, env_overrides=None):
        env = {k: v for k, v in os.environ.items() if not k.startswith("HORIZUN_")}
        env.update(env_overrides or {})
        self.proc = subprocess.Popen([server], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, env=env)
        self.next_id = 1
        self.initialize = self.request("initialize", {
            "protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "horizun-eval", "version": "1"}})
        self.notify("notifications/initialized", {})

    def _send(self, msg):
        self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def notify(self, method, params):
        self._send({"jsonrpc": "2.0", "method": method, "params": params})

    def request(self, method, params):
        rid = self.next_id
        self.next_id += 1
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError("server closed stdout")
            msg = json.loads(line)
            if msg.get("id") == rid:
                if "error" in msg:
                    raise RuntimeError(json.dumps(msg["error"]))
                return msg["result"]

    def tools(self):
        return self.request("tools/list", {})["tools"]

    def call(self, name, arguments):
        """Returns (is_error, structured_payload_or_text, raw_text)."""
        res = self.request("tools/call", {"name": name, "arguments": arguments})
        text = "".join(c.get("text", "") for c in res.get("content", []) if c.get("type") == "text")
        payload = res.get("structuredContent")
        if payload is None:
            try:
                payload = json.loads(text)
            except ValueError:
                payload = text
        return bool(res.get("isError")), payload, text

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=30)
        except Exception:
            self.proc.kill()
