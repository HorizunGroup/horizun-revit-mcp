// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// save_document save_local: what the measurements prove, decided in one place.
//
// Field report (cloud workshared model, Revit 2024): save_local answered
// verification_unknown on every save. A cloud model's PathName is not a file, and when
// its local copy is not found in the collaboration cache (CloudCacheLocator) there are
// no bytes to compare. One more piece of evidence was available all along and unused:
// Revit's own unsaved-changes flag. Document.IsModified going from true to false across
// Save() is Revit stating that the pending changes were written; Revit clears it only
// when the save completes. It is weaker than a byte comparison and is reported as such
// (its own outcome, saved_by_revit_state), but it is evidence, not nothing.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public sealed class SaveLocalOutcome
    {
        public string Outcome;
        public string Means;
        /// <summary>True only when a write was shown: by the bytes, or by Revit's flag clearing.</summary>
        public bool Saved;
        /// <summary>bytes | revit_flag | none.</summary>
        public string EvidenceStrength;
        /// <summary>When the outcome rests on the flag, the sentence that says so (else null).</summary>
        public string FlagEvidence;
    }

    public static class SaveLocalVerdict
    {
        public const string SavedVerified = "saved_verified";
        public const string SavedByRevitState = "saved_by_revit_state";
        public const string NothingToSave = "nothing_to_save";
        public const string VerificationFailed = "verification_failed";
        public const string VerificationUnknown = "verification_unknown";

        /// <summary>Document.IsModified went true -> false across Save(): Revit says the pending changes were written.</summary>
        public static bool FlagCleared(bool? modifiedBefore, bool? modifiedAfter) =>
            modifiedBefore == true && modifiedAfter == false;

        public static SaveLocalOutcome Decide(bool? bytesChanged, bool? modifiedBefore, bool? modifiedAfter, bool cloud)
        {
            bool cleared = FlagCleared(modifiedBefore, modifiedAfter);
            const string flagSentence = "Document.IsModified was true before Save() and false after it: Revit cleared its own " +
                                        "unsaved-changes flag, which it does only when the save completes.";
            if (bytesChanged == true)
                return new SaveLocalOutcome
                {
                    Outcome = SavedVerified, Saved = true, EvidenceStrength = "bytes",
                    FlagEvidence = cleared ? flagSentence : null,
                    Means = "The file's bytes changed between before and after, so a write demonstrably happened."
                };
            if (bytesChanged == null)
            {
                if (cleared)
                    return new SaveLocalOutcome
                    {
                        Outcome = SavedByRevitState, Saved = true, EvidenceStrength = "revit_flag", FlagEvidence = flagSentence,
                        Means = (cloud
                                    ? "This is a cloud model and its local copy could not be measured on disk. "
                                    : "The file could not be read before or after the save. ") + flagSentence +
                                " That is Revit's own statement that the pending changes were written to the local file; " +
                                "it is weaker than a byte comparison, and a local save is never a synchronize with central."
                    };
                return new SaveLocalOutcome
                {
                    Outcome = VerificationUnknown, Saved = false, EvidenceStrength = "none",
                    Means = "Revit's Save() returned without throwing, and this command could NOT read the file to " +
                            "find out whether anything was written" +
                            (modifiedBefore == false ? " (the document reported no unsaved changes before the call)" :
                             modifiedBefore == null || modifiedAfter == null ? " (Document.IsModified could not be read)" :
                             " (Document.IsModified stayed true after the save)") +
                            ". That is not a failure and it is not a success: it is an unmeasured save. Do not record " +
                            "this document as saved on the strength of it."
                };
            }
            if (modifiedBefore == false)
                // Rarer than it looks. Measured on Revit 2026: Save() rewrites the file even when
                // IsModified was already false, so the usual outcome for an unmodified document is
                // saved_verified with changed bytes, not this.
                return new SaveLocalOutcome
                {
                    Outcome = NothingToSave, Saved = false, EvidenceStrength = "bytes",
                    Means = "The document reported no unsaved changes before the call and the file did not change. " +
                            "Nothing needed writing, and nothing was. This is a correct no-op, NOT evidence that any " +
                            "earlier edit reached disk."
                };
            if (cloud && cleared)
                // The cache copy measured did not move while Revit says the save completed. The
                // copy measured may not be the one written (CloudCacheLocator picks by account
                // folder): conflicting evidence is unknown, not a failure and not a success.
                return new SaveLocalOutcome
                {
                    Outcome = VerificationUnknown, Saved = false, EvidenceStrength = "none", FlagEvidence = flagSentence,
                    Means = "Conflicting evidence: " + flagSentence + " But the cloud model's local copy found in the " +
                            "collaboration cache did not change, and it may not be the copy Revit wrote. Do not record " +
                            "this document as saved on the strength of this call; check cloud_cache."
                };
            return new SaveLocalOutcome
            {
                Outcome = VerificationFailed, Saved = false, EvidenceStrength = "bytes",
                Means = "The document reported UNSAVED CHANGES before this call (IsModified=true), Revit's Save() " +
                        "returned without throwing, and the file on disk did not change. Something was pending and " +
                        "is not on disk. Do not treat this document as saved."
            };
        }
    }
}
