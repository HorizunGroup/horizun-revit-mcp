// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Durable at-most-once admission for every typed mutation.
//
// A post-commit reply can be lost. Re-reading after Commit proves the answer was
// true when it was made, but does not help a caller that never received it: a
// normal retry would execute the mutation again. This append-only ledger closes
// that gap. A key is claimed before command code runs and the complete result is
// appended afterwards. On restart:
//
//   completed claim -> replay the recorded answer; execute nothing
//   claim only      -> outcome unknown; REFUSE to guess or run again
//   different hash -> conflict; execute nothing
//
// A torn final append is deliberately equivalent to claim-only. Losing certainty
// must fail closed, not turn into permission to repeat a write.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public enum DurableCommandOutcome { Fresh, Replay, Conflict, InDoubt }

    public sealed class DurableCommandDecision
    {
        public DurableCommandOutcome Outcome { get; internal set; }
        public string Key { get; internal set; }
        public string Command { get; internal set; }
        public string Fingerprint { get; internal set; }
        public string Path { get; internal set; }
        public CommandResult ReplayResult { get; internal set; }
        public string Message { get; internal set; }
        public bool IsFresh => Outcome == DurableCommandOutcome.Fresh;
    }

    public sealed class DurableCommandLedger
    {
        private readonly Func<string> _dir;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<int> _pid;

        public DurableCommandLedger(Func<string> directory = null, Func<DateTime> utcNow = null,
                                    Func<int> processId = null)
        {
            _dir = directory ?? HorizunPaths.IdempotencyDir;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _pid = processId ?? (() => { try { return Process.GetCurrentProcess().Id; } catch { return 0; } });
        }

        public DurableCommandDecision Claim(string key, string command, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("An idempotency key is required.", "key");
            if (key.Length > 200)
                throw new ArgumentException("idempotency_key cannot exceed 200 characters.", "key");

            string keyHash = RequestFingerprint.Sha256Hex(key);
            string dir = _dir();
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, keyHash + ".jsonl");

            return WithKeyLock(keyHash, () =>
            {
                JObject claim = null;
                JObject completed = null;
                string ledgerError;
                List<JObject> records = ReadRecords(path, out ledgerError);
                foreach (JObject record in records)
                {
                    string ev = (string)record["event"];
                    if (ev == "claimed")
                    {
                        if (claim != null || completed != null) { ledgerError = "duplicate or out-of-order claim"; break; }
                        if (string.IsNullOrWhiteSpace((string)record["command"]) ||
                            string.IsNullOrWhiteSpace((string)record["fingerprint"]))
                        { ledgerError = "claim is missing command or fingerprint"; break; }
                        claim = record;
                    }
                    else if (ev == "completed")
                    {
                        if (claim == null || completed != null) { ledgerError = "completion without one preceding claim"; break; }
                        if (!string.Equals((string)record["command"], (string)claim["command"], StringComparison.Ordinal) ||
                            !string.Equals((string)record["fingerprint"], (string)claim["fingerprint"], StringComparison.Ordinal))
                        { ledgerError = "completion does not match its claim"; break; }
                        completed = record;
                    }
                    else { ledgerError = "unknown ledger event"; break; }
                }

                if (ledgerError != null)
                    return new DurableCommandDecision
                    {
                        Outcome = DurableCommandOutcome.InDoubt,
                        Key = key, Command = command, Fingerprint = fingerprint, Path = path,
                        Message = "The durable idempotency record for key '" + key + "' already exists but is " +
                                  "unreadable or corrupt (" + ledgerError + "). Horizun cannot prove whether the " +
                                  "operation ran, so it will NOT execute it. Inspect the model and the ledger file " +
                                  path + " before deliberately choosing a new key."
                    };

                if (claim == null)
                {
                    Append(path, new JObject
                    {
                        ["event"] = "claimed",
                        ["command"] = command,
                        ["fingerprint"] = fingerprint,
                        ["pid"] = _pid(),
                        ["at_utc"] = _utcNow().ToString("o")
                    });
                    return new DurableCommandDecision
                    {
                        Outcome = DurableCommandOutcome.Fresh,
                        Key = key, Command = command, Fingerprint = fingerprint, Path = path
                    };
                }

                string oldCommand = (string)claim["command"];
                string oldFingerprint = (string)claim["fingerprint"];
                if (!string.Equals(oldCommand, command, StringComparison.Ordinal) ||
                    !string.Equals(oldFingerprint, fingerprint, StringComparison.Ordinal))
                    return new DurableCommandDecision
                    {
                        Outcome = DurableCommandOutcome.Conflict,
                        Key = key, Command = command, Fingerprint = fingerprint, Path = path,
                        Message = "idempotency_key '" + key + "' already identifies a DIFFERENT operation (" +
                                  (oldCommand ?? "unknown command") + ", claimed " +
                                  ((string)claim["at_utc"] ?? "at an unknown time") + "). Nothing ran. " +
                                  "Keep a key only for an identical retry; generate a new UUID for deliberate new work."
                    };

                if (completed != null)
                {
                    bool success = completed.Value<bool?>("success") == true;
                    CommandResult result = success
                        ? CommandResult.Ok(completed["data"]?.DeepClone())
                        : CommandResult.Fail((string)completed["error"] ?? "The recorded operation failed.");
                    if (completed["revit_said"] != null)
                        result.RevitSaid = completed["revit_said"].DeepClone();
                    return new DurableCommandDecision
                    {
                        Outcome = DurableCommandOutcome.Replay,
                        Key = key, Command = command, Fingerprint = fingerprint, Path = path,
                        ReplayResult = result,
                        Message = "Recorded result replayed; command code did not execute again."
                    };
                }

                return new DurableCommandDecision
                {
                    Outcome = DurableCommandOutcome.InDoubt,
                    Key = key, Command = command, Fingerprint = fingerprint, Path = path,
                    Message = "idempotency_key '" + key + "' was claimed for this exact operation by Revit pid " +
                              ((string)claim["pid"] ?? "unknown") + " at " +
                              ((string)claim["at_utc"] ?? "an unknown time") +
                              ", but no durable completion record exists. The process may have died after changing " +
                              "the model and before recording its answer. Horizun will NOT repeat an outcome it " +
                              "cannot know. Inspect the model, then use a new key only if you deliberately decide " +
                              "the operation must run."
                };
            });
        }

        public void Complete(DurableCommandDecision claim, CommandResult result)
        {
            if (claim == null || !claim.IsFresh) return;
            if (result == null) result = CommandResult.Fail("Command produced no result.");
            string keyHash = RequestFingerprint.Sha256Hex(claim.Key);
            WithKeyLock(keyHash, () =>
            {
                var record = new JObject
                {
                    ["event"] = "completed",
                    ["command"] = claim.Command,
                    ["fingerprint"] = claim.Fingerprint,
                    ["success"] = result.Success,
                    ["at_utc"] = _utcNow().ToString("o")
                };
                if (result.Success)
                    record["data"] = result.Data == null ? JValue.CreateNull() : JToken.FromObject(result.Data);
                else
                    record["error"] = result.Error;
                if (result.RevitSaid != null) record["revit_said"] = JToken.FromObject(result.RevitSaid);
                Append(claim.Path, record);
                return 0;
            });
        }

        private static T WithKeyLock<T>(string keyHash, Func<T> work)
        {
            string mutexName = "Local\\Horizun.Revit.Idempotency." + keyHash.Substring(0, 32);
            using (var mutex = new Mutex(false, mutexName))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Timed out waiting for the idempotency ledger lock.");
                    return work();
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static List<JObject> ReadRecords(string path, out string error)
        {
            error = null;
            var records = new List<JObject>();
            if (!File.Exists(path)) return records;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex) { error = ex.Message; return records; }
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { records.Add(JObject.Parse(line)); }
                catch (Exception ex)
                {
                    error = "invalid JSON line: " + ex.Message;
                    return records;
                }
            }
            return records;
        }

        private static void Append(string path, JObject record)
        {
            string line = record.ToString(Formatting.None) + Environment.NewLine;
            byte[] bytes = new UTF8Encoding(false).GetBytes(line);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
