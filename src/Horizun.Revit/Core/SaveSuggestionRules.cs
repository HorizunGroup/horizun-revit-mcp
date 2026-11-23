// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// LP-CP-5: SUGGEST THE LOCAL SAVE AFTER N WRITES - NEVER SAVE BY SURPRISE.
//
// On a heavy shared model the person saves the local file after every small batch, so a
// crash costs one batch and not the afternoon. The bridge can count what it wrote and say
// "save now"; it cannot decide for them: a save writes the person's file, takes seconds on
// a big model and is a good moment for nobody but the person to choose. So this only counts
// and suggests. It never calls Save, and the reply says so.
//
//   * a committed write (not a rehearsal) adds to the count of its document;
//   * a verified save_local, or Revit reporting no unsaved change (the person saved by
//     hand), resets it;
//   * from the Nth write on, the reply of each write carries save_suggestion with the count
//     and the exact call that saves. Below N, nothing is added.
//
// N is 10 by default; HORIZUN_SAVE_SUGGEST_AFTER overrides it and 0 turns the suggestion off.
// Revit-free: the command layer feeds it the facts.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class SaveSuggestionRules
    {
        public const int DefaultThreshold = 10;
        public const string ThresholdVariable = "HORIZUN_SAVE_SUGGEST_AFTER";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The threshold from the environment: default when unset or unreadable, 0 = off.</summary>
        public static int ThresholdFrom(string environmentValue)
        {
            if (string.IsNullOrWhiteSpace(environmentValue)) return DefaultThreshold;
            return int.TryParse(environmentValue.Trim(), out int n) && n >= 0 ? n : DefaultThreshold;
        }

        public static void RecordWrite(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate) Counts[key] = (Counts.TryGetValue(key, out int n) ? n : 0) + 1;
        }

        /// <summary>INT-9 M-3: the document is now known under another key (first save, Save As): its count follows it, the destination keeps only that.</summary>
        public static void Rename(string oldKey, string newKey)
        {
            lock (Gate) LedgerRenameRules.MoveAll(oldKey, newKey, Counts);
        }

        public static void RecordSave(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Gate) Counts.Remove(key);
        }

        public static int WritesSinceSave(string key)
        {
            lock (Gate) return Counts.TryGetValue(key ?? "", out int n) ? n : 0;
        }

        /// <summary>
        /// The block for a reply, or null below the threshold. <paramref name="isModified"/> false means Revit
        /// holds no unsaved change: whatever was counted is already on disk, so the count resets and nothing is said.
        /// </summary>
        public static JObject Block(string key, int threshold, bool? isModified, string documentTitle)
        {
            if (threshold <= 0 || string.IsNullOrEmpty(key)) return null;
            if (isModified == false) { RecordSave(key); return null; }
            int n = WritesSinceSave(key);
            if (n < threshold) return null;
            return new JObject
            {
                ["writes_since_local_save"] = n,
                ["threshold"] = threshold,
                ["suggestion"] = n + " committed write(s) are only in memory. Save the local file before the next batch: " +
                                 "horizun_save_document operation=save_local, target_document=" + (documentTitle ?? "<the document>") + ".",
                ["saves_by_itself"] = false,
                ["means"] = "A suggestion, never an action: the bridge does not save, synchronize or release on its own; the person (or the " +
                            "caller they asked) decides when. HORIZUN_SAVE_SUGGEST_AFTER changes N; 0 turns this off."
            };
        }

        internal static void ClearForTests() { lock (Gate) Counts.Clear(); }
    }
}
