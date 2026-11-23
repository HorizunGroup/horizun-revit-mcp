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
//   path ALREADY OPEN in this session, no detach
//     and no audit                               -> activation only: no upgrade guard
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

        /// <summary>The file's own path, when it is a file on disk. Null for cloud.</summary>
        public string Path;

        /// <summary>
        /// The central the file RECORDS (BasicFileInfo.CentralPath), null when unread. A file
        /// that reads as a central but records another file as its central is either a copy of
        /// that central or a local made by CreateNewLocal and never opened: Revit reports both
        /// alike (MEASURED 2026-09-27 in Revit 2026: IsCentral=true, IsLocal=false, IsCreatedLocal=false).
        /// </summary>
        public string CentralPath;

        /// <summary>
        /// The requested path is ALREADY OPEN in this Revit session (DocIdentity.SamePath against
        /// the open documents). Then nothing is opened and nothing is upgraded by this call: the
        /// document is activated as it is in memory. The disk header can still say an older year
        /// - an earlier open upgraded it in memory and nobody saved - and asking for
        /// allow_upgrade to ACTIVATE it (course dry run 2026-09-30, defect #13) asked permission
        /// for an upgrade that had already happened and that this call could not cause.
        /// </summary>
        public bool AlreadyOpenInSession;
    }

    /// <summary>What the caller asked for, reduced to the flags that decide.</summary>
    public sealed class OpenIntent
    {
        public string ExpectedVersion;
        public bool ExpectedVersionRequired;
        public bool AllowUpgrade;
        public bool Detach;
        public bool OpenCentral;

        /// <summary>audit=true asks for an OPEN option, which an activation cannot apply.</summary>
        public bool Audit;
    }

    public sealed class OpenVerdict
    {
        /// <summary>Null when the open may proceed. The whole sentence when it may not.</summary>
        public string Refusal { get; internal set; }
        /// <summary>Stable error code of the refusal: entrada_invalida for a bad expected_version,
        /// precondicion_no_cumplida when the file, the host or the central forbid the open.</summary>
        public string RefusalCode { get; internal set; }

        public bool Ok => Refusal == null;

        /// <summary>True when going ahead WILL upgrade the file. Never true for cloud: unknowable.</summary>
        public bool WillUpgrade { get; internal set; }

        /// <summary>
        /// "checked", "not_applicable_cloud" or "not_applicable_already_open". Never blank,
        /// never implied.
        /// </summary>
        public string VersionGuard { get; internal set; }

        /// <summary>How the central guard was satisfied, for the response to report.</summary>
        public string CentralGuard { get; internal set; }

        /// <summary>
        /// True when the path is already open and the request asks for nothing an open would
        /// have to apply: the caller ACTIVATES that document instead of opening it again.
        /// </summary>
        public bool ActivationOnly { get; internal set; }
    }

    public static class OpenDecision
    {
        public static OpenVerdict Decide(OpenFacts f, OpenIntent i)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            if (i == null) throw new ArgumentNullException(nameof(i));

            bool activationOnly = IsActivationOnly(f, i);
            var v = new OpenVerdict
            {
                VersionGuard = f.IsCloud ? "not_applicable_cloud"
                             : activationOnly ? "not_applicable_already_open"
                             : "checked",
                ActivationOnly = activationOnly
            };
            string name = string.IsNullOrEmpty(f.DisplayName) ? "this model" : f.DisplayName;

            // --- The stated belief, checked against the HOST first. ----------------
            // Cheapest and most useful: the file can be the right version and the caller
            // can still be talking to the Revit next door.
            string expected = NormalizeVersion(i.ExpectedVersion);
            if (expected == null && !string.IsNullOrWhiteSpace(i.ExpectedVersion))
                return Refuse(v,
                    "expected_version does not contain a Revit year: '" + i.ExpectedVersion + "'. Pass something " +
                    "like '2026'. Nothing was opened.", "entrada_invalida");

            if (expected == null && i.ExpectedVersionRequired)
                return RefuseInput(v,
                    "expected_version is required for open and must contain a Revit year (e.g. '2026'). " +
                    "It is the whole safety mechanism: opening a file on a newer host upgrades it irreversibly, " +
                    "and this tool has no way to know which bridge you meant to be on unless you say so.");

            if (expected != null && !SameVersion(f.HostVersion, expected))
            {
                int fileYear, hostYear;
                bool older = !f.IsCloud && TryYear(f.FileVersion, out fileYear) &&
                    TryYear(f.HostVersion, out hostYear) && fileYear < hostYear;
                return Refuse(v,
                    "This Revit host is " + (f.HostVersion ?? "(unreadable)") + ", not " + expected +
                    ". You are talking to the wrong bridge. Nothing was opened. Send this to the bridge running " +
                    "Revit " + expected + " - horizun_target lists the ones that are running." +
                    (!f.IsCloud ? " The detected file version is " + (f.FileVersion ?? "unreadable") + "." : "") +
                    (older ? " To open the older file here instead, set expected_version to the host year " +
                        f.HostVersion + " and allow_upgrade=true; back up the file before converting it." : ""));
            }

            // --- Guard 1: the irreversible upgrade. --------------------------------
            // Not for an activation: the document is already in memory, and whatever its
            // disk header says, this call opens nothing and so upgrades nothing.
            if (!f.IsCloud && !activationOnly)
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

                if (f.IsCentral == true && RecordsAnotherCentral(f.Path, f.CentralPath))
                    return Refuse(v,
                        "REFUSING TO OPEN: '" + name + "' reads as a workshared CENTRAL model, but the central it " +
                        "records is '" + f.CentralPath + "', not this file. Revit reports a COPY of that central and " +
                        "a new local made by CreateNewLocal that was never opened the same way (IsCentral=true, " +
                        "IsLocal=false), so this cannot tell them apart. A new local becomes a local on disk once " +
                        "Revit opens it and SAVES it (opened and closed without saving it still reads as a " +
                        "central). If it is a copy of the central, detach=true reads it safely; open_central=true " +
                        "opens it as it is, and anything synchronized from it goes to the central it records. " +
                        "For a local you made yourself with CreateNewLocal that is what you want: open_central names " +
                        "what Revit reports about the file (a central), it does not open the central model itself. " +
                        "Nothing was opened.");

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

        /// <summary>
        /// Is this request an ACTIVATION of a document already open in the session rather than
        /// an open? Only a local path that is already open, and only when the caller asked for
        /// nothing that exists solely as an open option: detach=true wants a detached COPY, and
        /// audit=true an audited open - activating the document already in memory would hand back
        /// something else, so those go through the full guard as before.
        /// </summary>
        public static bool IsActivationOnly(OpenFacts f, OpenIntent i)
            => f != null && i != null && !f.IsCloud && f.AlreadyOpenInSession && !i.Detach && !i.Audit;

        // ------------------------------------------------------------------ small
        /// <summary>
        /// Does the file record a central OTHER than itself? Only when both paths are known and,
        /// compared as full paths ignoring case, differ; a server or cloud central ("RSN://",
        /// "...://") is another central by definition. Unknown is not "another": it answers false,
        /// and the plain central refusal stands.
        /// </summary>
        public static bool RecordsAnotherCentral(string path, string centralPath)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(centralPath)) return false;
            if (centralPath.Contains("://")) return true;
            try
            {
                return !string.Equals(System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'),
                                      System.IO.Path.GetFullPath(centralPath).TrimEnd('\\', '/'),
                                      StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

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

        private static OpenVerdict Refuse(OpenVerdict v, string message, string code = "precondicion_no_cumplida")
        {
            v.Refusal = message;
            v.RefusalCode = code;
            return v;
        }

        private static OpenVerdict RefuseInput(OpenVerdict v, string message) => Refuse(v, message, "entrada_invalida");
    }
}
