// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The first eight bytes of a file, and what they mean, for when BasicFileInfo
// cannot read it (story 5.26).
//
// WHY THIS EXISTS. Revit's own message for an unreadable header is:
//
//   "The file is a newer format file where the structure of the BasicFileInfo
//    storage has changed, or the file is saved in very old version of Revit
//    without basic file data."
//
// Both halves of that sentence are about REVIT FILES, so it pushes every reader
// towards a version problem. Measured on 2026-08-13 it cost two false diagnoses
// in a row - "it is a newer Revit" (written into a client's documentation on
// 2026-08-07 and believed for six days), then "the download is corrupt" (the
// file was re-downloaded and arrived byte-identical, 337,648,956 bytes, matching
// the storageSize the cloud API reports). The answer took eight bytes read by
// hand: 50 4b 03 04, "PK\x03\x04" - a ZIP. It was a package of seven models
// published with a .rvt extension and one model's name.
//
// It is not a one-off: a later sweep of 3,252 files across 153 projects found
// 1,193 such entries in 40 projects. Every one of them is diagnosed wrong by the
// sentence above, and right by this file.
//
// WHAT IT REFUSES TO DO: interpret bytes it does not know. An unrecognised
// signature comes back as hex with no story attached, because "unknown" is a
// fact and a guess dressed as a fact is what this whole codebase exists to
// avoid. Reading the bytes is also NOT a claim that the file opens - an OLE
// header means the container is the one Revit uses, nothing more.
//
// Revit-free on purpose: the file IO is the caller's, the rule is arithmetic
// over bytes, and every case that matters is provable without a Revit.
// -----------------------------------------------------------------------------
using System;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class FileSignature
    {
        /// <summary>How many leading bytes are read and reported.</summary>
        public const int Bytes = 8;

        /// <summary>
        /// The OLE2 compound-file header. A real .rvt/.rfa is an OLE compound file,
        /// so this says the CONTAINER is Revit's - not that the file is undamaged,
        /// and not that this Revit can open it.
        /// </summary>
        public const string Ole = "d0cf11e0a1b11ae1";

        /// <summary>Local file header of a ZIP archive: PK\x03\x04.</summary>
        public const string Zip = "504b0304";

        /// <summary>Empty archive (end-of-central-directory first) and spanned archive.</summary>
        public const string ZipEmpty = "504b0506";
        public const string ZipSpanned = "504b0708";

        /// <summary>
        /// Lower-case hex of up to <see cref="Bytes"/> leading bytes, or null when
        /// there was nothing to read. A file shorter than eight bytes returns what
        /// it has - the length is part of the evidence.
        /// </summary>
        public static string ToHex(byte[] head)
        {
            if (head == null || head.Length == 0) return null;
            int n = Math.Min(head.Length, Bytes);
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++) sb.Append(head[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// What that signature means, in a sentence a person can act on - or an
        /// explicit "unknown" that interprets nothing. Null hex answers null: no
        /// bytes were read, and that is not the same as bytes that mean nothing.
        /// </summary>
        public static string Describe(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            string h = hex.ToLowerInvariant();

            if (h.StartsWith(Ole, StringComparison.Ordinal))
                return "OLE compound file (d0cf11e0a1b11ae1) - this IS the container a Revit .rvt/.rfa uses. " +
                       "So the file is a Revit file whose header this Revit could not parse: a year whose " +
                       "BasicFileInfo storage differs, or a truncated/damaged file. The version story is worth " +
                       "believing HERE, and only here.";

            if (h.StartsWith(Zip, StringComparison.Ordinal))
                return "ZIP (PK\\x03\\x04) - NOT a Revit model. Something zipped was given a .rvt name: measured " +
                       "in the field, a package of seven models published to ACC under one model's file name. " +
                       "Unzip it and look inside; do not read this as a Revit version problem.";

            if (h.StartsWith(ZipEmpty, StringComparison.Ordinal))
                return "ZIP, empty archive (PK\\x05\\x06) - NOT a Revit model, and it contains nothing either.";

            if (h.StartsWith(ZipSpanned, StringComparison.Ordinal))
                return "ZIP, spanned archive (PK\\x07\\x08) - NOT a Revit model, and this is one volume of a " +
                       "multi-part archive.";

            if (h.Length < Bytes * 2)
                return "Only " + (h.Length / 2) + " byte(s) could be read - the file is shorter than a header. " +
                       "An interrupted download or an empty placeholder looks exactly like this. Unrecognised " +
                       "signature: nothing is claimed about what it is.";

            return "Unrecognised signature. This is neither the OLE container a Revit file uses (" + Ole +
                   ") nor a ZIP (" + Zip + "). Nothing is claimed about what the file is - these are the raw " +
                   "bytes, read so you can decide instead of guessing.";
        }

        /// <summary>True only for the OLE container a genuine Revit file uses.</summary>
        public static bool LooksLikeRevit(string hex)
            => !string.IsNullOrEmpty(hex) && hex.ToLowerInvariant().StartsWith(Ole, StringComparison.Ordinal);
    }
}
