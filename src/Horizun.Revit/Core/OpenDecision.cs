// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Whether a model may be opened, decided out of FACTS rather than out of Revit.
//
// This is the half of OpenGuard that can be proved. Reading a file header needs
// BasicFileInfo and opening a document needs a UIApplication, but the rules - is a
// newer file openable, is an unreadable version a clearance, does detach excuse a
// central - are arithmetic over four strings and four booleans. Kept in a Revit
// file they were only ever exercised by opening real models on a real machine,
// which is why the two commands could carry different versions of them for months
// without anybody noticing: nothing compared the tables, because there was no table
// to compare, only two sequences of if-statements in two large files.
//
// So the facts are gathered by OpenGuard (which needs Revit) and the decision is
// made here (which does not). The whole table is one method, and every branch of it
// is a test.
//
// THE TABLE, stated once so it can be read without following the code:
//
//   expected_version absent, and required        -> refuse
//   expected_version present, != host            -> refuse (the wrong bridge)
//   cloud model                                  -> version unknowable, guard skipped
//   file version unreadable, no allow_upgrade    -> refuse (unknown is not a match)
//   file NEWER than host                         -> refuse ALWAYS (no downgrade exists)
//   file OLDER than host, no allow_upgrade       -> refuse (the irreversible upgrade)
//   central, no detach and no open_central       -> refuse
//   central UNKNOWN, no detach and no open_central -> refuse (unknown is not a no)
//   otherwise                                    -> open it
// -----------------------------------------------------------------------------
using System;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>What is known about a model before anything opens it.</summary>
    public sealed class OpenFacts
    {
        /// <summary>A model in ACC / BIM 360. Its saved version cannot be read up front.</summary>
        public bool IsCloud;

        /// <summary>The Revit running right now. Null when even that could not be read.</summary>
        public string HostVersion;

        /// <summary>The file's own saved version, or null for unknown. Always null for cloud.</summary>
        public string FileVersion;

        /// <summary>Why the header could not be read, when it could not. For the message only.</summary>
        public string ReadError;

        /// <summary>
        /// Is this the workshared CENTRAL model? null is UNKNOWN and is treated as yes,
        /// because an unreadable central flag is not a document known to be safe. Always
        /// true for a cloud model: that is what a model in ACC is.
        /// </summary>
        public bool? IsCentral;

        /// <summary>Named in messages so the caller knows which file is being refused.</summary>
        public string DisplayName = "this model";
    }

    /// <summary>What the caller asked for, reduced to the flags that decide.</summary>
    public sealed class OpenIntent
    {
        public string ExpectedVersion;
        public bool ExpectedVersionRequired;
        public bool AllowUpgrade;
        public bool Detach;
        public bool OpenCentral;
    }

    public sealed class OpenVerdict
    {
        /// <summary>Null when the open may proceed. The whole sentence when it may not.</summary>
        public string Refusal { get; internal set; }

        public bool Ok => Refusal == null;

        /// <summary>True when going ahead WILL upgrade the file. Never true for cloud: unknowable.</summary>
        public bool WillUpgrade { get; internal set; }

        /// <summary>"checked" or "not_applicable_cloud". Never blank, never implied.</summary>
        public string VersionGuard { get; internal set; }

        /// <summary>How the central guard was satisfied, for the response to report.</summary>
        public string CentralGuard { get; internal set; }
    }

    public static class OpenDecision
    {
        public static OpenVerdict Decide(OpenFacts f, OpenIntent i)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            if (i == null) throw new ArgumentNullException(nameof(i));

            var v = new OpenVerdict { VersionGuard = f.IsCloud ? "not_applicable_cloud" : "checked" };
            string name = string.IsNullOrEmpty(f.DisplayName) ? "this model" : f.DisplayName;

            // --- The stated belief, checked against the HOST first. ----------------
            // Cheapest and most useful: the file can be the right version and the caller
            // can still be talking to the Revit next door.
            string expected = NormalizeVersion(i.ExpectedVersion);
            if (expected == null && !string.IsNullOrWhiteSpace(i.ExpectedVersion))
                return Refuse(v,
                    "expected_version does not contain a Revit year: '" + i.ExpectedVersion + "'. Pass something " +
                    "like '2026'. Nothing was opened.");

            if (expected == null && i.ExpectedVersionRequired)
                return Refuse(v,
                    "expected_version is required for open and must contain a Revit year (e.g. '2026'). " +
                    "It is the whole safety mechanism: opening a file on a newer host upgrades it irreversibly, " +
                    "and this tool has no way to know which bridge you meant to be on unless you say so.");

            if (expected != null && !SameVersion(f.HostVersion, expected))
                return Refuse(v,
                    "This Revit host is " + (f.HostVersion ?? "(unreadable)") + ", not " + expected +
                    ". You are talking to the wrong bridge. Nothing was opened. Send this to the bridge running " +
                    "Revit " + expected + " - horizun_target lists the ones that are running.");

            // --- Guard 1: the irreversible upgrade. --------------------------------
            if (!f.IsCloud)
            {
                bool versionKnown = !string.IsNullOrEmpty(f.FileVersion);
                bool sameVersion = versionKnown && SameVersion(f.FileVersion, f.HostVersion);
                v.WillUpgrade = versionKnown && !sameVersion;

                // "I could not look" is not "there is nothing there". An unreadable version
                // is exactly when a blind open does the damage, so it needs the same opt-in.
                if (!versionKnown && !i.AllowUpgrade)
                    return Refuse(v,
                        "REFUSING TO OPEN: '" + name + "' does not report a readable Revit version" +
                        (f.ReadError == null ? "" : " (" + f.ReadError + ")") +
                        ". This is a refusal, not a failure to check - an unknown version is not a matching " +
                        "version, and opening it on the wrong host would upgrade it with no way back. Nothing was " +
                        "opened. Pass allow_upgrade=true only if you are willing to have it converted to Revit " +
                        (f.HostVersion ?? "(this host)") + ".");

                if (versionKnown && !sameVersion)
                {
                    int fileYear, hostYear;
                    bool fileYearKnown = TryYear(f.FileVersion, out fileYear);
                    bool hostYearKnown = TryYear(f.HostVersion, out hostYear);
                    bool comparable = fileYearKnown && hostYearKnown;

                    // A NEWER file cannot be opened by an older Revit at all, and no flag
                    // changes that - there is no downgrade. Saying so is the difference
                    // between a sentence the caller can act on and Revit's own error about
                    // a file format, arriving after allow_upgrade was passed in hope.
                    if (comparable && fileYear > hostYear)
                        return Refuse(v,
                            "REFUSING TO OPEN: '" + name + "' was saved in Revit " + f.FileVersion +
                            " and this is Revit " + f.HostVersion + ". A newer file cannot be opened by an older " +
                            "Revit at all, and allow_upgrade CANNOT help - there is no downgrade. Nothing was " +
                            "opened. Open it on a Revit " + f.FileVersion + " bridge; horizun_target lists the " +
                            "ones that are running.");

                    if (!i.AllowUpgrade)
                        return Refuse(v,
                            "REFUSING TO OPEN: '" + name + "' was saved in Revit " + f.FileVersion +
                            " and this is Revit " + f.HostVersion + ". Opening it here would UPGRADE the file " +
                            "permanently - there is no way back, and a batch would do it to every file it " +
                            "touches. The " + f.FileVersion + " original stops existing the moment it is saved. " +
                            "Nothing was opened. Open it in Revit " + f.FileVersion + ", or pass " +
                            "allow_upgrade=true if converting it is what you actually want, and back it up first.");
                }
            }

            // --- Guard 2: the central model. ---------------------------------------
            bool cleared = i.Detach || i.OpenCentral;
            if (f.IsCentral != false && !cleared)
            {
                if (f.IsCloud)
                    return Refuse(v,
                        "REFUSING TO OPEN: a model in ACC / BIM 360 is the CENTRAL model - it is the thing the " +
                        "team synchronizes to. Opening it directly means working in it, and the fact that it " +
                        "lives in the cloud rather than on a server share does not make it less shared. Pass " +
                        "detach=true to read a detached copy (this is what an audit or a read-only pass wants), " +
                        "or open_central=true if you genuinely mean to open the central itself. Nothing was " +
                        "opened. THIS GUARD IS NEW for cloud opens: it was applied to central models on disk and " +
                        "not to the same models in the cloud.");

                if (f.IsCentral == true)
                    return Refuse(v,
                        "REFUSING TO OPEN: '" + name + "' is a workshared CENTRAL model. Opening it directly " +
                        "means working in the file everyone else synchronizes to. Pass detach=true to read a " +
                        "detached copy (safe), or open_central=true if you genuinely mean to open the central " +
                        "itself. Nothing was opened.");

                // Unknown is not a clearance, here as everywhere else in this codebase.
                return Refuse(v,
                    "REFUSING TO OPEN: whether '" + name + "' is a workshared CENTRAL model could not be read" +
                    (f.ReadError == null ? "" : " (" + f.ReadError + ")") + ", and an unreadable central flag is " +
                    "not a 'no'. If it IS a central, opening it directly means working in the file everyone else " +
                    "synchronizes to. Pass detach=true to read a detached copy (safe), or open_central=true to " +
                    "proceed regardless. Nothing was opened.");
            }

            v.CentralGuard = f.IsCentral == false ? "not_a_central"
                           : i.Detach ? "detached"
                           : "open_central";
            return v;
        }

        // ------------------------------------------------------------------ small
        /// <summary>A Revit year out of anything that contains one, or null.</summary>
        public static string NormalizeVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            Match m = Regex.Match(s, @"(19|20)\d{2}");
            return m.Success ? m.Value : null;
        }

        public static bool SameVersion(string a, string b)
        {
            string na = NormalizeVersion(a), nb = NormalizeVersion(b);
            return na != null && nb != null && string.Equals(na, nb, StringComparison.Ordinal);
        }

        public static bool TryYear(string s, out int year)
        {
            year = 0;
            string n = NormalizeVersion(s);
            return n != null && int.TryParse(n, out year);
        }

        private static OpenVerdict Refuse(OpenVerdict v, string message)
        {
            v.Refusal = message;
            return v;
        }
    }
}
