// A stand-in for OpenAI's tunnel-client, for scripts/chatgpt-tunnel.tests.ps1.
//
// It reproduces the behaviour the helper depends on, as MEASURED on the official
// 0.0.14 binaries - not a friendlier version of it:
//
//   * --version; the runtime variant says "flavor=runtime-cloudflared".
//   * init --help / init / doctor exist only in the full variant; the runtime one
//     answers `unknown command "init" for "tunnel-client-runtime-cloudflared"`.
//   * init re-parses --mcp-command with tunnel-client's POSIX-like parser (a
//     backslash escapes, a single quote opens a quote) and refuses a command whose
//     executable does not exist - so a wrongly serialized path fails HERE exactly
//     as it fails with OpenAI's client.
//   * run writes its loopback health URL to --health.url-file and serves /readyz
//     (always 200, as the real one does while polls fail) and /metrics with
//     commands_poll_last_successful_timestamp_seconds, fresh, stale, 0 or absent.
//
// Behaviour is chosen by files beside the executable: fake-mode.txt
// (full | runtime | hang | errhelp | exitonrun) and fake-poll.txt
// (fresh | stale | never | absent). Every invocation appends its argv and
// whether CONTROL_PLANE_API_KEY was present (never its value) to fake-argv.log.
//
// An executable whose name contains "mcp-server" is instead a minimal MCP stdio
// server; one whose name contains "echo-args" prints its argv, one per line.
//
// C# 5, so the csc.exe that ships with .NET Framework compiles it on any Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class FakeTunnelClient
{
    static string Dir { get { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); } }

    static string ReadSetting(string name, string fallback)
    {
        string p = Path.Combine(Dir, name);
        if (!File.Exists(p)) return fallback;
        string v = File.ReadAllText(p).Trim();
        return v.Length == 0 ? fallback : v;
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string self = Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().Location).ToLowerInvariant();
        if (self.Contains("mcp-server")) return McpServer();
        if (self.Contains("echo-args")) { foreach (string a in args) Console.WriteLine(a); return 0; }

        bool keyPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONTROL_PLANE_API_KEY"));
        File.AppendAllText(Path.Combine(Dir, "fake-argv.log"),
            string.Join(" ", args) + " || KEY=" + (keyPresent ? "present" : "absent") + Environment.NewLine, Encoding.UTF8);

        string mode = ReadSetting("fake-mode.txt", "full");
        bool runtime = mode == "runtime";
        string flavorName = runtime ? "tunnel-client-runtime-cloudflared" : "tunnel-client";

        if (mode == "hang") { Thread.Sleep(120000); return 0; }
        if (args.Length == 0) { Console.Error.WriteLine("usage"); return 1; }

        if (args[0] == "--version")
        {
            if (runtime) Console.WriteLine("0.0.99 git sha: fake go: go1.27.0 build flags: -trimpath flavor=runtime-cloudflared");
            else Console.WriteLine("0.0.99+fake (git sha: fake)");
            return 0;
        }
        if (runtime && args[0] != "run")
        {
            Console.Error.WriteLine("Error: unknown command \"" + args[0] + "\" for \"" + flavorName + "\"");
            return 1;
        }
        if (mode == "errhelp" && args[0] == "init")
        {
            Console.Error.WriteLine("Error: unknown flag: --mcp-command");
            return 1;
        }

        Dictionary<string, string> flags = new Dictionary<string, string>();
        HashSet<string> switches = new HashSet<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--")) continue;
            if (a == "--help" || a == "--force" || a == "--explain") { switches.Add(a); continue; }
            if (i + 1 < args.Length) { flags[a] = args[i + 1]; i++; }
        }

        if (args[0] == "init")
        {
            if (switches.Contains("--help"))
            {
                Console.WriteLine("Create a runnable first-use tunnel-client profile");
                Console.WriteLine("Flags:");
                Console.WriteLine("      --mcp-command string                 MCP command for the generated profile");
                Console.WriteLine("      --profile-dir string                 Profile directory override");
                return 0;
            }
            string cmd; flags.TryGetValue("--mcp-command", out cmd);
            List<string> argv;
            string err = ParseCommandArgv(cmd ?? "", out argv);
            if (err != null) { Console.Error.WriteLine("Error: mcp-command preflight failed: " + err); return 1; }
            if (!File.Exists(argv[0]))
            {
                Console.Error.WriteLine("Error: mcp-command preflight failed: stdio MCP executable \"" + argv[0] + "\" was not found");
                return 1;
            }
            string dir = flags.ContainsKey("--profile-dir") ? flags["--profile-dir"] : Path.Combine(Dir, "default-profiles");
            string name = flags.ContainsKey("--profile") ? flags["--profile"] : "sample_mcp_with_dcr";
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, name + ".yaml");
            if (File.Exists(file) && !switches.Contains("--force")) { Console.Error.WriteLine("Error: profile already exists: " + file); return 1; }
            string listen = flags.ContainsKey("--health-listen-addr") ? flags["--health-listen-addr"] : "127.0.0.1:8080";
            File.WriteAllText(file,
                "config_version: 1\ncontrol_plane:\n  tunnel_id: \"" + (flags.ContainsKey("--tunnel-id") ? flags["--tunnel-id"] : "") + "\"\n" +
                "health:\n  listen_addr: \"" + listen + "\"\nmcp:\n  commands:\n    - channel: main\n      command: " + JsonString(cmd) + "\n",
                new UTF8Encoding(false));
            Console.WriteLine("wrote " + file);
            return 0;
        }

        string profileDir = flags.ContainsKey("--profile-dir") ? flags["--profile-dir"] : Path.Combine(Dir, "default-profiles");
        string profile = flags.ContainsKey("--profile") ? flags["--profile"] : "";
        string profileFile = Path.Combine(profileDir, profile + ".yaml");

        if (args[0] == "doctor")
        {
            if (!File.Exists(profileFile)) { Console.Error.WriteLine("doctor: profile not found: " + profileFile); return 1; }
            Console.WriteLine("doctor: profile " + profile + " ok");
            return 0;
        }

        if (args[0] == "run")
        {
            if (switches.Contains("--help")) { Console.WriteLine("Usage of run:"); return 0; }
            if (!runtime && !File.Exists(profileFile)) { Console.Error.WriteLine("run: profile not found: " + profileFile); return 1; }
            if (!keyPresent) { Console.Error.WriteLine("run: CONTROL_PLANE_API_KEY is required"); return 1; }
            if (mode == "exitonrun") { Console.Error.WriteLine("run: simulated startup failure"); return 3; }
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string logFile; flags.TryGetValue("--log.file", out logFile);
            if (!string.IsNullOrEmpty(logFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile));
                File.AppendAllText(logFile, "fake tunnel-client started on port " + port + Environment.NewLine);
            }
            string urlFile; flags.TryGetValue("--health.url-file", out urlFile);
            if (!string.IsNullOrEmpty(urlFile)) File.WriteAllText(urlFile, "http://127.0.0.1:" + port, new UTF8Encoding(false));
            while (true)
            {
                TcpClient c = listener.AcceptTcpClient();
                try { Serve(c); } catch { }
            }
        }

        Console.Error.WriteLine("Error: unknown command \"" + args[0] + "\" for \"" + flavorName + "\"");
        return 1;
    }

    static void Serve(TcpClient c)
    {
        using (c)
        using (NetworkStream s = c.GetStream())
        {
            StreamReader r = new StreamReader(s, Encoding.ASCII);
            string first = r.ReadLine() ?? "";
            string line;
            while (!string.IsNullOrEmpty(line = r.ReadLine())) { }
            string[] parts = first.Split(' ');
            string path = parts.Length > 1 ? parts[1] : "/";
            string body; int code = 200;
            if (path.StartsWith("/readyz")) body = "ok";
            else if (path.StartsWith("/metrics"))
            {
                string poll = ReadSetting("fake-poll.txt", "fresh");
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                StringBuilder sb = new StringBuilder();
                sb.Append("# HELP tunnel_client_up up\ntunnel_client_up 1\n");
                if (poll == "fresh") sb.Append("commands_poll_last_successful_timestamp_seconds " + now.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\n");
                else if (poll == "stale") sb.Append("commands_poll_last_successful_timestamp_seconds " + (now - 600).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "\n");
                else if (poll == "never") sb.Append("commands_poll_last_successful_timestamp_seconds 0\n");
                body = sb.ToString();
            }
            else { code = 404; body = "not found"; }
            byte[] b = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + code + (code == 200 ? " OK" : " Not Found") + "\r\nContent-Type: text/plain\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(b, 0, b.Length);
        }
    }

    // tunnel-client pkg/runtimeconfig parseCommandArgv, faithfully.
    static string ParseCommandArgv(string raw, out List<string> result)
    {
        result = new List<string>();
        string input = raw.Trim();
        if (input.Length == 0) return "command is empty";
        StringBuilder b = new StringBuilder();
        bool inSingle = false, inDouble = false, escaped = false;
        foreach (char ch in input)
        {
            if (escaped) { b.Append(ch); escaped = false; continue; }
            if (inSingle) { if (ch == '\'') inSingle = false; else b.Append(ch); continue; }
            if (inDouble)
            {
                if (ch == '\\') escaped = true;
                else if (ch == '"') inDouble = false;
                else b.Append(ch);
                continue;
            }
            if (ch == '\\') escaped = true;
            else if (ch == '\'') inSingle = true;
            else if (ch == '"') inDouble = true;
            else if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') { if (b.Length > 0) { result.Add(b.ToString()); b.Length = 0; } }
            else b.Append(ch);
        }
        if (escaped) return "unterminated escape sequence";
        if (inSingle || inDouble) return "unterminated quoted string";
        if (b.Length > 0) result.Add(b.ToString());
        if (result.Count == 0) return "command is empty";
        return null;
    }

    static string JsonString(string v)
    {
        if (v == null) return "\"\"";
        StringBuilder b = new StringBuilder("\"");
        foreach (char ch in v)
        {
            if (ch == '"') b.Append("\\\"");
            else if (ch == '\\') b.Append("\\\\");
            else b.Append(ch);
        }
        return b.Append('"').ToString();
    }

    static int McpServer()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        string line;
        while ((line = Console.In.ReadLine()) != null)
        {
            string id = null;
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, "\"id\"\\s*:\\s*(\\d+)");
            if (m.Success) id = m.Groups[1].Value;
            if (id == null) continue;
            if (line.Contains("\"initialize\""))
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{\"listChanged\":true}},\"serverInfo\":{\"name\":\"fake-horizun\",\"version\":\"0\"}}}");
            else if (line.Contains("\"tools/list\""))
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"horizun_health\",\"inputSchema\":{\"type\":\"object\"}}]}}");
            else
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32601,\"message\":\"method not found\"}}");
            Console.Out.Flush();
        }
        return 0;
    }
}
