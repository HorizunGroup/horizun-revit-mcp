// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Save the active document, and prove it landed.
//
// "Saved" is one of the easiest lies to tell: Document.Save() returns void, so a
// handler that calls it and reports success is reporting that it did not throw —
// not that the file changed. This one stats the file BEFORE and AFTER and reports
// both timestamps and both sizes, so the claim is backed by the filesystem.
//
// Two refusals, both deliberate:
//   * A document that has never been saved has no path. Saving it means INVENTING
//     one, and this command does not choose where your model lives. Refused.
//   * A workshared document: Save() writes the LOCAL file. It does not send a
//     thing to central. Reporting "saved" to someone who meant "synced" is how
//     a day of work is lost, so the answer says which one happened, loudly.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class SaveDocumentCommand : ICommand
    {
        public string Name => "horizun_save_document";

        public string Description =>
            "Save the ACTIVE document and verify it against the filesystem: the file's timestamp and size are " +
            "read before and after, and reported. Refuses a document that was never saved (it will not invent a " +
            "path) and never calls SaveAs. On a workshared model this saves the LOCAL file only — it is NOT a " +
            "synchronize with central, and the response says so.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string expectedTitle = null;
            JObject req = null;
            try
            {
                req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                expectedTitle = req.Value<string>("expected_document");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // WHICH model. expected_document was an OPTIONAL guard here and is now required
            // through the shared gate, which also covers the two cases a title comparison
            // never could: a name matching more than one open document, and a name matching
            // one that is open but NOT active. Saving the wrong model is not recoverable by
            // saving again.
            GateResult gate = DocumentGate.ForMutation(app, req, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;

            string path = doc.PathName;
            if (string.IsNullOrEmpty(path))
            {
                return CommandResult.Fail(
                    "This document has never been saved, so it has no path. Saving it would mean choosing where " +
                    "your model lives, and this command does not do that. Save it once from Revit, then call this.");
            }

            // THE PREVENTION GATE, BEFORE THE FILE IS TOUCHED. Optional: without
            // require_gate this command behaves exactly as it always has. With it, the
            // audit's checks run on the document as it stands, the caller's profile is
            // evaluated with the audit's own evaluator, and a blocked or not-assessable
            // decision refuses HERE - above doc.Save() - so a refused save leaves the
            // file's bytes exactly as they were. The reply carries the decision either
            // way, and names the save paths this gate does not reach.
            OperationGateResult gateDecision = OperationGate.Evaluate(app, doc, req["require_gate"],
                                                                      GatedOperation.Save, Name);
            if (gateDecision.Refusal != null) return gateDecision.Refusal;

            // IsModified is measured, not assumed. Its BEFORE value is what makes the
            // difference between "nothing needed writing" and "something did and was not
            // written", which the old version could not tell apart because it reported
            // saved=true for both.
            bool? modifiedBefore = null;
            try { modifiedBefore = doc.IsModified; } catch { }

            DateTime? beforeStamp = null; long? beforeSize = null;
            ReadFileFacts(path, out beforeStamp, out beforeSize);
            string hashBefore = HashOf(path);

            try
            {
                doc.Save();
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Revit refused to save: " + ex.Message);
            }

            DateTime? afterStamp = null; long? afterSize = null;
            ReadFileFacts(path, out afterStamp, out afterSize);
            string hashAfter = HashOf(path);

            bool? modifiedAfter = null;
            try { modifiedAfter = doc.IsModified; } catch { }

            // ---- Did the bytes move? Hash first; stat only when hashing could not run. ----
            //
            // A timestamp and a size can both stay put across a real write, and a
            // timestamp alone can move without the content changing. The hash settles it
            // when it is available, and when it is not the answer is UNKNOWN rather than
            // whatever the weaker signal happened to say.
            bool? bytesChanged = null;
            string evidence;
            if (hashBefore != null && hashAfter != null)
            {
                bytesChanged = hashBefore != hashAfter;
                evidence = "sha256 of the file, read before and after";
            }
            else if (beforeStamp.HasValue && afterStamp.HasValue)
            {
                bytesChanged = afterStamp.Value != beforeStamp.Value || afterSize != beforeSize;
                evidence = "timestamp and size only - the file could not be hashed, so a write that left both " +
                           "unchanged would not be visible here";
            }
            else
            {
                evidence = "none - the file could not be read before or after";
            }

            // ---- The outcome. saved=true means DEMONSTRATED, and nothing else. ----
            string outcome;
            string means;
            if (bytesChanged == null)
            {
                outcome = "verification_unknown";
                means = "Revit's Save() returned without throwing, and this command could NOT read the file to " +
                        "find out whether anything was written. That is not a failure and it is not a success: " +
                        "it is an unmeasured save. Do not record this document as saved on the strength of it.";
            }
            else if (bytesChanged == true)
            {
                outcome = "saved_verified";
                means = "The file's bytes changed between before and after, so a write demonstrably happened.";
            }
            else if (modifiedBefore == false)
            {
                // Rarer than it looks. Measured on Revit 2026: Save() rewrites the file
                // even when IsModified was already false, so the usual outcome for an
                // unmodified document is saved_verified with changed bytes, not this. This
                // branch is for the case where Revit genuinely writes nothing.
                outcome = "nothing_to_save";
                means = "The document reported no unsaved changes before the call and the file did not change. " +
                        "Nothing needed writing, and nothing was. This is a correct no-op, NOT evidence that any " +
                        "earlier edit reached disk.";
            }
            else
            {
                outcome = "verification_failed";
                means = "The document reported UNSAVED CHANGES before this call (IsModified=true), Revit's Save() " +
                        "returned without throwing, and the file on disk did not change. Something was pending and " +
                        "is not on disk. Do not treat this document as saved.";
            }

            bool demonstrated = outcome == "saved_verified";

            var payload = new JObject
            {
                // Only ever true when the write was demonstrated. It used to be a constant.
                ["saved"] = demonstrated,
                ["outcome"] = outcome,
                ["outcome_means"] = means,
                ["document"] = doc.Title,
                ["path"] = path,
                ["is_workshared"] = SafeWorkshared(doc),
                ["what_this_did"] = SafeWorkshared(doc) == true
                    ? "Saved the LOCAL file. This is NOT a synchronize with central: nothing was sent to the " +
                      "central model and no other user can see these changes yet."
                    : "Saved the file in place.",
                ["was_modified_before"] = modifiedBefore.HasValue ? (JToken)modifiedBefore.Value : JValue.CreateNull(),
                ["is_modified_after"] = modifiedAfter.HasValue ? (JToken)modifiedAfter.Value : JValue.CreateNull(),
                ["modified_flags_note"] =
                    "null means Document.IsModified could not be read, which is UNKNOWN and never false.",
                ["bytes_changed_on_disk"] = bytesChanged.HasValue ? (JToken)bytesChanged.Value : JValue.CreateNull(),
                ["verified_by"] = evidence,
                ["sha256_before"] = hashBefore,
                ["sha256_after"] = hashAfter,
                ["file_before"] = new JObject { ["modified_utc"] = beforeStamp, ["size_bytes"] = beforeSize },
                ["file_after"] = new JObject { ["modified_utc"] = afterStamp, ["size_bytes"] = afterSize }
            };
            if (gateDecision.Requested) payload["prevention"] = gateDecision.Prevention;

            // A save that cannot be shown to have written is not reported as one. The
            // caller gets the whole measurement either way - a refusal that throws the
            // evidence away is its own kind of unhelpful.
            if (outcome == "verification_failed")
                return CommandResult.Fail(means + " Full measurement: " +
                                          payload.ToString(Newtonsoft.Json.Formatting.None));

            return CommandResult.Ok(payload);
        }

        /// <summary>
        /// SHA-256 of the file, or null when it cannot be read. Null is UNKNOWN.
        ///
        /// Capped: a central model can run to gigabytes, and hashing one twice per save
        /// would cost more than the save. Over the cap this returns null and the caller
        /// falls back to timestamp and size, saying which it used.
        /// </summary>
        private const long MaxHashBytes = 512L * 1024 * 1024;

        private static string HashOf(string path)
        {
            try
            {
                var fi = new System.IO.FileInfo(path);
                if (!fi.Exists || fi.Length > MaxHashBytes) return null;

                // FileShare.ReadWrite, not OpenRead(). Measured: OpenRead() throws on the
                // document Revit currently has open - which is EVERY document this command
                // is ever pointed at - so the hash was always null and every save fell back
                // to timestamp and size. The strong evidence was unreachable in exactly the
                // case it exists for. Sharing write access lets the read succeed while
                // Revit holds the file.
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                                                         System.IO.FileAccess.Read,
                                                         System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                    return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        private static bool? SafeWorkshared(Document d)
        {
            try { return d.IsWorkshared; } catch { return null; }
        }

        private static void ReadFileFacts(string path, out DateTime? stamp, out long? size)
        {
            stamp = null; size = null;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                stamp = fi.LastWriteTimeUtc;
                size = fi.Length;
            }
            catch { /* unreadable: both stay null, which the caller reports as unknown */ }
        }
    }
}
