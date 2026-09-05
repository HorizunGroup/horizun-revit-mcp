// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH PLACEMENT of a drawing an update is about - and whether it has moved.
//
// A drawing is not one thing in a model. It is a FILE (bytes on disk, or none,
// when the import is embedded) placed one or more times, each placement its own
// ImportInstance with its own transform. The first provenance record folded all
// of that into one irreversible fingerprint, and two questions then had no
// answer:
//
//   - a file linked TWICE - which is how a repeated wing is drawn - gives both
//     placements the same file hash, so an update for one claimed and orphaned
//     the other's elements (backlog 8.4d);
//   - an import with no external file has no path and no hash, so every element
//     it produced fell out of scope and the update reported "0 of everything"
//     as if it had looked (backlog 8.4c).
//
// So provenance now keeps the three identities APART - file, placement,
// transform - and this file decides what each combination means. It never
// touches Revit: the decision that can claim somebody else's wing is provable
// at a desk, which is the only place it should be argued.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One placement of a drawing in the model, as measured. The Revit-free
    /// half of CadInstanceFacts: only what identity and scope need.
    /// </summary>
    public sealed class CadPlacement
    {
        public long ElementId;
        /// <summary>The ImportInstance UniqueId - the only thing that separates two placements of one file.</summary>
        public string PlacementId;
        public string FileSha256;
        public string ExternalPath;
        /// <summary>A path is recorded and nothing is there. Not the same as "no path".</summary>
        public bool FileMissing;
        public string FileError;
        public bool? IsLinked;
        /// <summary>The first-version fingerprint, kept so a v1 record can still be matched exactly.</summary>
        public string SourceFingerprint;
        public string TransformFingerprint;
        public double[] OriginMm;
        public double[] BasisX;
        public double[] BasisY;
        public double Scale = 1.0;

        public string EncodedOrigin => CadPlacementRules.EncodeOrigin(OriginMm);
        public string EncodedBasis => CadPlacementRules.EncodeBasis(BasisX, BasisY, Scale);
    }

    /// <summary>How this run knows which drawing it is looking at, and how sure that is.</summary>
    public sealed class CadSourceIdentity
    {
        /// <summary>file_hash | embedded_placement | source_file_missing | source_unhashable</summary>
        public string Mode;
        public string FileSha256;
        public string ExternalPath;
        public string PlacementId;
        public string Says;

        public JObject ToJson() => new JObject
        {
            ["mode"] = Mode,
            ["file_sha256"] = FileSha256,
            ["source_hash"] = FileSha256 ?? "unavailable",
            ["external_path"] = ExternalPath,
            ["placement_id"] = PlacementId,
            ["means"] = Says
        };
    }

    /// <summary>
    /// A placement's frame: where the drawing's origin sits and which way its
    /// axes point, in model millimetres. Enough to carry a point built under one
    /// frame to where it would be under another.
    /// </summary>
    public sealed class CadPlacementFrame
    {
        public double[] Origin;   // mm
        public double[] BasisX;
        public double[] BasisY;
        public double[] BasisZ;
        public double Scale = 1.0;

        public static CadPlacementFrame From(double[] origin, double[] basisX, double[] basisY, double scale)
        {
            if (origin == null || basisX == null || basisY == null) return null;
            if (origin.Length < 3 || basisX.Length < 3 || basisY.Length < 3) return null;
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
            return new CadPlacementFrame
            {
                Origin = new[] { origin[0], origin[1], origin[2] },
                BasisX = new[] { basisX[0], basisX[1], basisX[2] },
                BasisY = new[] { basisY[0], basisY[1], basisY[2] },
                BasisZ = Cross(basisX, basisY),
                Scale = scale
            };
        }

        /// <summary>Model point → drawing-local coordinates.</summary>
        public double[] ToLocal(CadPoint p)
        {
            double dx = p.X - Origin[0], dy = p.Y - Origin[1], dz = p.Z - Origin[2];
            return new[]
            {
                (dx * BasisX[0] + dy * BasisX[1] + dz * BasisX[2]) / Scale,
                (dx * BasisY[0] + dy * BasisY[1] + dz * BasisY[2]) / Scale,
                (dx * BasisZ[0] + dy * BasisZ[1] + dz * BasisZ[2]) / Scale
            };
        }

        /// <summary>Drawing-local coordinates → model point.</summary>
        public CadPoint FromLocal(double[] l)
        {
            double lx = l[0] * Scale, ly = l[1] * Scale, lz = l[2] * Scale;
            return new CadPoint(
                Origin[0] + lx * BasisX[0] + ly * BasisY[0] + lz * BasisZ[0],
                Origin[1] + lx * BasisX[1] + ly * BasisY[1] + lz * BasisZ[1],
                Origin[2] + lx * BasisX[2] + ly * BasisY[2] + lz * BasisZ[2]);
        }

        private static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]
        };
    }

    /// <summary>
    /// Whether a placement sits where it sat when its elements were built, and
    /// by how much it does not.
    /// </summary>
    public sealed class CadPlacementMove
    {
        public bool Moved;
        public string RecordedFingerprint;
        public string CurrentFingerprint;
        public CadPlacementFrame From;
        public CadPlacementFrame To;
        public double[] DeltaMm;
        public double RotationDegrees;
        public double ScaleRatio = 1.0;
        /// <summary>Set when the recorded frame cannot be decoded: the move is known, its size is not.</summary>
        public string DeltaUnknownBecause;

        /// <summary>Where a point built under the old frame would be if it had followed the placement.</summary>
        public CadPoint Carry(CadPoint asBuilt)
        {
            if (From == null || To == null) return asBuilt;
            return To.FromLocal(From.ToLocal(asBuilt));
        }

        public List<CadPoint> Carry(IEnumerable<CadPoint> asBuilt) =>
            asBuilt == null ? null : asBuilt.Select(Carry).ToList();

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["moved"] = Moved,
                ["recorded_transform"] = RecordedFingerprint,
                ["current_transform"] = CurrentFingerprint
            };
            if (DeltaMm != null)
                o["delta_mm"] = new JArray(DeltaMm.Select(v => Math.Round(v, 3)));
            else o["delta_mm"] = JValue.CreateNull();
            o["rotation_degrees"] = Math.Round(RotationDegrees, 4);
            o["scale_ratio"] = Math.Round(ScaleRatio, 6);
            if (DeltaUnknownBecause != null) o["delta_unknown_because"] = DeltaUnknownBecause;
            return o;
        }
    }

    /// <summary>One element this run will not claim, and the two things it could belong to.</summary>
    public sealed class CadScopeExclusion
    {
        public long ElementId;
        public string Reason;
        public string Says;
        public JObject ToJson() => new JObject { ["element_id"] = ElementId, ["reason"] = Reason, ["says"] = Says };
    }

    /// <summary>
    /// WHICH ELEMENTS THIS RUN IS ABOUT, decided once and carried into the
    /// planner as a set of ids rather than as a predicate over hashes. A
    /// predicate over hashes is what claimed the other wing.
    /// </summary>
    public sealed class CadUpdateScope
    {
        public const string Identified = "identified";
        public const string Unidentified = "scope_unidentified";
        public const string AmbiguousLineage = "supersedes_ambiguous";

        /// <summary>v2 records whose placement is this one, or one the caller named as superseded.</summary>
        public HashSet<long> Claimed = new HashSet<long>();
        /// <summary>v1 records this run may claim because exactly one placement of that file could have built them.</summary>
        public HashSet<long> MigratedFromV1 = new HashSet<long>();
        /// <summary>v1 records that two or more placements could have built. Never claimed, never orphaned.</summary>
        public List<CadScopeExclusion> AmbiguousV1 = new List<CadScopeExclusion>();
        /// <summary>v2 records of the same file under a placement this run was not told about. Untouched.</summary>
        public HashSet<long> OtherPlacement = new HashSet<long>();
        /// <summary>Superseded-by-file elements that split across two placements, so the file alone cannot say which.</summary>
        public List<CadScopeExclusion> AmbiguousLineageElements = new List<CadScopeExclusion>();
        public int Unrelated;
        public string Verdict = Identified;
        public JObject LookedFor = new JObject();
        public JObject Exists = new JObject();

        /// <summary>The legacy predicate: scoped by file hash and lineage, blind to placement. Kept for the rules tests that predate placement identity.</summary>
        public Func<CadAuditSubject, bool> LegacyPredicate;

        public bool Includes(CadAuditSubject s)
        {
            if (s?.Provenance == null) return false;
            if (LegacyPredicate != null) return LegacyPredicate(s);
            return Claimed.Contains(s.ElementId) || MigratedFromV1.Contains(s.ElementId);
        }

        public int ClaimableCount => Claimed.Count + MigratedFromV1.Count;

        public static CadUpdateScope ByFile(string sourceFileSha256, IEnumerable<string> lineage)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(sourceFileSha256)) known.Add(sourceFileSha256);
            if (lineage != null)
                foreach (string sha in lineage)
                    if (!string.IsNullOrWhiteSpace(sha)) known.Add(sha);
            return new CadUpdateScope
            {
                LegacyPredicate = s =>
                {
                    CadProvenance prov = s?.Provenance;
                    if (prov == null) return false;
                    if (known.Count == 0) return true;
                    if (string.IsNullOrEmpty(prov.SourceFileSha256)) return false;
                    return known.Contains(prov.SourceFileSha256);
                }
            };
        }

        public JObject ToJson() => new JObject
        {
            ["verdict"] = Verdict,
            ["claimed"] = Claimed.Count,
            ["migrated_from_v1"] = MigratedFromV1.Count,
            ["migrated_from_v1_ids"] = new JArray(MigratedFromV1.OrderBy(x => x).Take(200)),
            ["ambiguous_v1"] = new JArray(AmbiguousV1.Select(x => x.ToJson()).Take(100)),
            ["other_placement"] = OtherPlacement.Count,
            ["other_placement_ids"] = new JArray(OtherPlacement.OrderBy(x => x).Take(200)),
            ["ambiguous_lineage"] = new JArray(AmbiguousLineageElements.Select(x => x.ToJson()).Take(100)),
            ["unrelated"] = Unrelated,
            ["looked_for"] = LookedFor,
            ["exists"] = Exists
        };
    }

    public static class CadPlacementRules
    {
        public const string IdentityFileHash = "file_hash";
        public const string IdentityEmbedded = "embedded_placement";
        public const string IdentityFileMissing = "source_file_missing";
        public const string IdentityUnhashable = "source_unhashable";

        public const string RestampMigrated = "migrated_from_v1";
        public const string RestampPlacementMoved = "placement_moved_accepted";

        // ------------------------------------------------------------ identity

        /// <summary>
        /// Say WHICH identity this run is using, because they are not equally
        /// strong and a reader must know what a match means.
        /// </summary>
        public static CadSourceIdentity Identity(CadPlacement p)
        {
            var id = new CadSourceIdentity
            {
                FileSha256 = p?.FileSha256,
                ExternalPath = p?.ExternalPath,
                PlacementId = p?.PlacementId
            };
            if (p == null) { id.Mode = IdentityUnhashable; id.Says = "no placement was measured."; return id; }

            if (!string.IsNullOrEmpty(p.FileSha256))
            {
                id.Mode = IdentityFileHash;
                id.Says = "the drawing file is on this machine and was hashed. Elements are matched by this " +
                          "placement's id first, and by the file hash where the caller states a lineage.";
                return id;
            }
            if (string.IsNullOrWhiteSpace(p.ExternalPath))
            {
                id.Mode = IdentityEmbedded;
                id.Says = "this is an EMBEDDED import: no external file, so no path and no hash. Revit keeps no " +
                          "file it will hand back, so source_hash is unavailable. Identity is the placement's " +
                          "own id (" + (p.PlacementId ?? "(none)") + ") and its transform; elements are matched " +
                          "by that placement id, and a v1 record by its exact source fingerprint.";
                return id;
            }
            if (p.FileMissing)
            {
                id.Mode = IdentityFileMissing;
                id.Says = "the link records a path and nothing is there: " + p.ExternalPath + ". The file has " +
                          "moved or been deleted, so its bytes cannot be hashed. Revit still holds the geometry " +
                          "it last loaded, and this run plans against THAT, matched by placement id. A hash " +
                          "recorded on an earlier conversion cannot be confirmed from here.";
                return id;
            }
            id.Mode = IdentityUnhashable;
            id.Says = "the file at " + p.ExternalPath + " could not be hashed: " + (p.FileError ?? "(no reason given)") +
                      ". Elements are matched by placement id only.";
            return id;
        }

        // ------------------------------------------------------------ transform

        public static string EncodeOrigin(double[] origin)
        {
            if (origin == null || origin.Length < 3) return null;
            return string.Join(",", origin.Take(3).Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        public static string EncodeBasis(double[] basisX, double[] basisY, double scale)
        {
            if (basisX == null || basisY == null || basisX.Length < 3 || basisY.Length < 3) return null;
            return string.Join(",", basisX.Take(3).Select(v => v.ToString("0.#########", CultureInfo.InvariantCulture))) + ";" +
                   string.Join(",", basisY.Take(3).Select(v => v.ToString("0.#########", CultureInfo.InvariantCulture))) + ";" +
                   (scale <= 0 ? 1.0 : scale).ToString("0.#########", CultureInfo.InvariantCulture);
        }

        public static CadPlacementFrame DecodeFrame(string origin, string basis)
        {
            if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(basis)) return null;
            double[] o = Triple(origin);
            string[] parts = basis.Split(';');
            if (o == null || parts.Length < 2) return null;
            double[] bx = Triple(parts[0]), by = Triple(parts[1]);
            double scale = 1.0;
            if (parts.Length > 2)
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
            if (bx == null || by == null) return null;
            return CadPlacementFrame.From(o, bx, by, scale);
        }

        private static double[] Triple(string s)
        {
            string[] xyz = (s ?? "").Split(',');
            if (xyz.Length < 3) return null;
            var v = new double[3];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(xyz[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
            return v;
        }

        public static CadPlacementFrame Frame(CadPlacement p) =>
            p == null ? null : CadPlacementFrame.From(p.OriginMm, p.BasisX, p.BasisY, p.Scale);

        /// <summary>
        /// Has the placement moved since this record was written? The
        /// fingerprint decides; the frames say by how much.
        ///
        /// A record with no transform (v1) cannot answer: it is reported as
        /// "not moved" with the reason, because "unknown" here must not become
        /// "the drawing moved" and re-derive every element on a guess.
        /// </summary>
        public static CadPlacementMove CompareTransforms(CadProvenance recorded, CadPlacement current)
        {
            var move = new CadPlacementMove
            {
                RecordedFingerprint = recorded?.PlacementTransform,
                CurrentFingerprint = current?.TransformFingerprint
            };
            if (recorded == null || current == null) return move;
            if (string.IsNullOrEmpty(recorded.PlacementTransform) || string.IsNullOrEmpty(current.TransformFingerprint))
            {
                move.DeltaUnknownBecause = string.IsNullOrEmpty(recorded.PlacementTransform)
                    ? "the record does not carry a placement transform (written before provenance v2)"
                    : "the current placement's transform could not be read";
                return move;
            }
            if (string.Equals(recorded.PlacementTransform, current.TransformFingerprint, StringComparison.Ordinal))
                return move;

            move.Moved = true;
            move.From = DecodeFrame(recorded.PlacementOrigin, recorded.PlacementBasis);
            move.To = Frame(current);
            if (move.From == null || move.To == null)
            {
                move.DeltaUnknownBecause = "the fingerprints differ and " +
                    (move.From == null ? "the recorded frame" : "the current frame") + " could not be decoded";
                return move;
            }
            move.DeltaMm = new[]
            {
                move.To.Origin[0] - move.From.Origin[0],
                move.To.Origin[1] - move.From.Origin[1],
                move.To.Origin[2] - move.From.Origin[2]
            };
            move.RotationDegrees = PlanAngleDegrees(move.From.BasisX, move.To.BasisX);
            move.ScaleRatio = move.From.Scale > 0 ? move.To.Scale / move.From.Scale : 1.0;
            return move;
        }

        private static double PlanAngleDegrees(double[] a, double[] b)
        {
            double la = Math.Sqrt(a[0] * a[0] + a[1] * a[1]), lb = Math.Sqrt(b[0] * b[0] + b[1] * b[1]);
            if (la <= 1e-12 || lb <= 1e-12) return 0;
            double cos = Math.Max(-1, Math.Min(1, (a[0] * b[0] + a[1] * b[1]) / (la * lb)));
            return Math.Acos(cos) * 180.0 / Math.PI;
        }

        // ---------------------------------------------------------------- scope

        /// <summary>
        /// Decide which stamped elements THIS placement may claim.
        ///
        ///   v2, same placement id                 → claimed
        ///   v2, placement the caller named         → claimed (supersedes_placement_ids)
        ///   v2, file the caller named, ONE placement of it among the records
        ///                                          → claimed; TWO or more → ambiguous, refused
        ///   v2, same file, other placement         → other_placement: untouched, never orphaned
        ///   v1, exact source fingerprint           → migrated_from_v1 (same instance, same transform)
        ///   v1, file in scope, ≤1 placement of that file in the model
        ///                                          → migrated_from_v1
        ///   v1, file in scope, ≥2 placements       → ambiguous_v1, named, refused
        ///   anything else                          → unrelated
        ///
        /// A v1 record has no placement id, so "which placement built it" is
        /// answered by counting the placements of its file that exist NOW: with
        /// one, there is nothing else it could be; with two, guessing is claiming
        /// somebody's wing.
        /// </summary>
        public static CadUpdateScope Resolve(IList<CadAuditSubject> subjects, CadPlacement placement,
                                             IEnumerable<string> lineageHashes,
                                             IEnumerable<string> lineagePlacementIds,
                                             IList<CadPlacement> placementsInModel)
        {
            var scope = new CadUpdateScope();
            subjects = subjects ?? new List<CadAuditSubject>();
            placementsInModel = placementsInModel ?? new List<CadPlacement>();
            var lineage = new HashSet<string>(StringComparer.Ordinal);
            foreach (string sha in lineageHashes ?? new string[0])
                if (!string.IsNullOrWhiteSpace(sha)) lineage.Add(sha.Trim().ToLowerInvariant());
            var namedPlacements = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in lineagePlacementIds ?? new string[0])
                if (!string.IsNullOrWhiteSpace(id)) namedPlacements.Add(id.Trim());

            string thisPlacement = placement?.PlacementId;
            string thisSha = placement?.FileSha256;
            var knownFiles = new HashSet<string>(lineage, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(thisSha)) knownFiles.Add(thisSha);

            // How many placements of each file the MODEL holds now.
            var placementsByFile = new Dictionary<string, List<CadPlacement>>(StringComparer.Ordinal);
            foreach (CadPlacement p in placementsInModel)
            {
                if (p == null || string.IsNullOrEmpty(p.FileSha256)) continue;
                List<CadPlacement> bucket;
                if (!placementsByFile.TryGetValue(p.FileSha256, out bucket))
                    placementsByFile[p.FileSha256] = bucket = new List<CadPlacement>();
                bucket.Add(p);
            }

            // Placements the RECORDS name, per superseded file - for lineage by
            // file, which is only unambiguous when the records agree on one.
            var recordedPlacementsByFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (CadAuditSubject s in subjects)
            {
                CadProvenance p = s?.Provenance;
                if (p == null || string.IsNullOrEmpty(p.PlacementId) || string.IsNullOrEmpty(p.SourceFileSha256)) continue;
                HashSet<string> ids;
                if (!recordedPlacementsByFile.TryGetValue(p.SourceFileSha256, out ids))
                    recordedPlacementsByFile[p.SourceFileSha256] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(p.PlacementId);
            }

            var v1Files = new Dictionary<string, int>(StringComparer.Ordinal);
            var v2Placements = new Dictionary<string, int>(StringComparer.Ordinal);
            var v2PlacementFile = new Dictionary<string, string>(StringComparer.Ordinal);
            var otherFiles = new Dictionary<string, int>(StringComparer.Ordinal);
            int v1NoFile = 0;

            foreach (CadAuditSubject s in subjects)
            {
                CadProvenance p = s?.Provenance;
                if (p == null) continue;
                string sha = p.SourceFileSha256;

                if (!string.IsNullOrEmpty(p.PlacementId))
                {
                    Count(v2Placements, p.PlacementId);
                    if (!string.IsNullOrEmpty(sha)) v2PlacementFile[p.PlacementId] = sha;

                    if (thisPlacement != null && string.Equals(p.PlacementId, thisPlacement, StringComparison.Ordinal))
                    { scope.Claimed.Add(s.ElementId); continue; }
                    if (namedPlacements.Contains(p.PlacementId))
                    { scope.Claimed.Add(s.ElementId); continue; }

                    if (!string.IsNullOrEmpty(sha) && lineage.Contains(sha))
                    {
                        // LINEAGE BY FILE. Unambiguous only when every record of
                        // that file names ONE placement (minus any the caller named).
                        HashSet<string> ids = recordedPlacementsByFile[sha];
                        var unnamed = ids.Where(x => !namedPlacements.Contains(x)).ToList();
                        if (unnamed.Count <= 1) { scope.Claimed.Add(s.ElementId); continue; }
                        scope.AmbiguousLineageElements.Add(new CadScopeExclusion
                        {
                            ElementId = s.ElementId,
                            Reason = CadUpdateScope.AmbiguousLineage,
                            Says = "supersedes_sha256 names file " + Short(sha) + ", and the model's records " +
                                   "were built from " + unnamed.Count + " placements of it (" +
                                   string.Join(", ", unnamed.Take(4)) + "). The file alone cannot say which " +
                                   "this revision replaces: name it in supersedes_placement_ids."
                        });
                        continue;
                    }

                    if (!string.IsNullOrEmpty(sha) && string.Equals(sha, thisSha, StringComparison.Ordinal))
                    { scope.OtherPlacement.Add(s.ElementId); continue; }

                    scope.Unrelated++;
                    if (!string.IsNullOrEmpty(sha)) Count(otherFiles, sha);
                    continue;
                }

                // --- a record with no placement identity (provenance v1) ---
                // COUNTED FIRST, WHATEVER HAPPENS TO IT NEXT. exists.v1_files is the
                // census of legacy provenance still in this model, and it used to be
                // written only on the slow path: a v1 record identified by its exact
                // source fingerprint - the most confident case, and the one an update
                // is usually about - returned before it was counted. Measured live on
                // 2026-09-03: four records migrated from v1 and the census reported
                // {}, which reads as "no legacy provenance here" to anyone deciding
                // whether a migration is still pending.
                if (!string.IsNullOrEmpty(sha)) Count(v1Files, sha);

                if (!string.IsNullOrEmpty(p.SourceFingerprint) && placement != null &&
                    string.Equals(p.SourceFingerprint, placement.SourceFingerprint, StringComparison.Ordinal))
                {
                    // The v1 fingerprint folds instance id, bytes, path AND
                    // transform. Equal means this very placement, unmoved - the
                    // one identity a v1 record can prove.
                    scope.MigratedFromV1.Add(s.ElementId);
                    continue;
                }

                if (string.IsNullOrEmpty(sha)) { v1NoFile++; scope.Unrelated++; continue; }
                if (!knownFiles.Contains(sha)) { scope.Unrelated++; Count(otherFiles, sha); continue; }

                List<CadPlacement> holders;
                int placementsOfFile = placementsByFile.TryGetValue(sha, out holders) ? holders.Count : 0;
                if (placementsOfFile <= 1) { scope.MigratedFromV1.Add(s.ElementId); continue; }

                scope.AmbiguousV1.Add(new CadScopeExclusion
                {
                    ElementId = s.ElementId,
                    Reason = "ambiguous_v1",
                    Says = "built from file " + Short(sha) + " before provenance recorded WHICH placement, and " +
                           "the model now holds " + placementsOfFile + " placements of that file (instances " +
                           string.Join(", ", holders.Select(h => h.ElementId + " [" + (h.PlacementId ?? "?") + "]").Take(4)) +
                           "). Any of them could have built it. Not claimed, not orphaned, not deleted: " +
                           "convert it again under one placement, or remove the placement that did not build it."
                });
            }

            scope.LookedFor = new JObject
            {
                ["placement_id"] = thisPlacement,
                ["file_sha256"] = thisSha,
                ["source_fingerprint"] = placement?.SourceFingerprint,
                ["supersedes_sha256"] = new JArray(lineage),
                ["supersedes_placement_ids"] = new JArray(namedPlacements)
            };
            scope.Exists = new JObject
            {
                ["v2_placements"] = new JObject(v2Placements.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => new JProperty(k.Key, new JObject
                    {
                        ["elements"] = k.Value,
                        ["file_sha256"] = v2PlacementFile.ContainsKey(k.Key) ? v2PlacementFile[k.Key] : null,
                        ["still_in_model"] = placementsInModel.Any(x => string.Equals(x?.PlacementId, k.Key, StringComparison.Ordinal))
                    }))),
                ["v1_files"] = new JObject(v1Files.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => new JProperty(k.Key, k.Value))),
                ["v1_without_file_hash"] = v1NoFile,
                ["placements_in_model"] = new JArray(placementsInModel.Where(x => x != null).Select(x => new JObject
                {
                    ["instance_id"] = x.ElementId,
                    ["placement_id"] = x.PlacementId,
                    ["file_sha256"] = x.FileSha256,
                    ["external_path"] = x.ExternalPath
                }))
            };

            scope.Verdict = scope.ClaimableCount > 0 ? CadUpdateScope.Identified : CadUpdateScope.Unidentified;
            return scope;
        }

        /// <summary>
        /// The refusal for a run that met a v1 record two placements could have
        /// built.
        ///
        /// MEASURED AT A DESK, 2026-09-03: this used to have no refusal of its
        /// own. An ambiguous v1 element is simply OUT OF SCOPE, so when anything
        /// else was claimable the plan went ahead - and the drawing entity that
        /// built the ambiguous element matched nothing in scope, so it came back
        /// as a `create`. Applying that plan builds a SECOND wall where the
        /// drawing shows one, on top of the one already standing. The only case
        /// that refused was the one where NOTHING was claimable, and it refused
        /// as `scope_unidentified` - whose closing advice is "use
        /// horizun_plan_from_cad if this really is a first conversion", which
        /// against a model that already holds the conversion builds a second copy
        /// of the whole building. The documented behaviour ("refused with both
        /// placements named") was the right one; the code did not have it.
        ///
        /// So: its own refusal, named, before anything else is decided. It names
        /// every placement that could have built each element, because "two
        /// placements could have built it" is not actionable until a reader knows
        /// WHICH two.
        /// </summary>
        public static string AmbiguousV1Refusal(CadUpdateScope scope, string documentTitle)
        {
            if (scope == null || scope.AmbiguousV1.Count == 0)
                return "ambiguous_v1: nothing is ambiguous.";
            return "ambiguous_v1: " + scope.AmbiguousV1.Count + " element(s) in '" + documentTitle + "' were " +
                   "stamped before provenance recorded WHICH placement built them, and the model holds more than " +
                   "one placement of their drawing. " +
                   string.Join(" ", scope.AmbiguousV1.Take(3).Select(x => "Element " + x.ElementId + ": " + x.Says)) +
                   (scope.AmbiguousV1.Count > 3 ? " (" + (scope.AmbiguousV1.Count - 3) + " more in scope.ambiguous_v1.)" : "") +
                   " NOTHING WAS PLANNED, and this is not a case that improves by planning the rest: " +
                   scope.ClaimableCount + " element(s) here would have been claimed, and the drawing entities " +
                   "behind the ambiguous ones match nothing in that scope - so they would come back as new work " +
                   "and the apply would build a second copy of them beside the ones already standing. Do NOT run " +
                   "horizun_plan_from_cad against this model either; it would build the whole drawing again. " +
                   "Settle which placement owns them first: delete or repoint the placement that did not build " +
                   "them so exactly one remains, or delete those elements and convert them again under one " +
                   "placement. Either way the next update claims them and rewrites their records as v2.";
        }

        /// <summary>
        /// The refusal for a run that can claim nothing, worded from what it
        /// looked for and what it found. This is the guard the backlog named:
        /// "0 of everything" is not a finding, it is a run that did not look.
        /// </summary>
        public static string UnidentifiedRefusal(CadUpdateScope scope, string documentTitle)
        {
            if (scope == null) return "scope_unidentified: nothing to scope by.";
            var v2 = scope.Exists["v2_placements"] as JObject;
            var v1 = scope.Exists["v1_files"] as JObject;
            var parts = new List<string>();
            if (v2 != null && v2.Count > 0)
                parts.Add(v2.Count + " placement(s) with v2 provenance (" +
                          string.Join(", ", v2.Properties().Take(4).Select(p =>
                              p.Name + ": " + p.Value.Value<int>("elements") + " element(s)" +
                              (p.Value.Value<bool?>("still_in_model") == true ? "" : ", instance no longer in the model"))) + ")");
            if (v1 != null && v1.Count > 0)
                parts.Add(v1.Count + " file(s) with v1 provenance (" +
                          string.Join(", ", v1.Properties().Take(4).Select(p => Short(p.Name) + ": " + p.Value + " element(s)")) + ")");
            if (scope.AmbiguousV1.Count > 0)
                parts.Add(scope.AmbiguousV1.Count + " v1 element(s) that two placements could have built");
            if (scope.AmbiguousLineageElements.Count > 0)
                parts.Add(scope.AmbiguousLineageElements.Count + " element(s) whose superseded file was placed more than once");
            if (scope.OtherPlacement.Count > 0)
                parts.Add(scope.OtherPlacement.Count + " element(s) of this same file under another placement");

            return "scope_unidentified: this run can claim NOTHING in '" + documentTitle + "'. It looked for " +
                   "placement " + (scope.LookedFor.Value<string>("placement_id") ?? "(none)") +
                   ", file " + Short(scope.LookedFor.Value<string>("file_sha256")) +
                   (((JArray)scope.LookedFor["supersedes_sha256"]).Count > 0
                       ? ", superseding " + string.Join(", ", ((JArray)scope.LookedFor["supersedes_sha256"]).Select(x => Short(x.ToString())))
                       : ", with no lineage stated") +
                   ". The model holds " + (parts.Count == 0 ? "no CAD provenance this run recognises" : string.Join("; ", parts)) +
                   ". Planning anyway would report zero changes about a conversion it never looked at, and " +
                   "build the drawing a second time. Say what this placement supersedes - supersedes_placement_ids " +
                   "for a placement, supersedes_sha256 for a file placed once - or use horizun_plan_from_cad if " +
                   "this really is a first conversion.";
        }

        private static void Count(Dictionary<string, int> d, string key)
        {
            int n;
            d[key] = d.TryGetValue(key, out n) ? n + 1 : 1;
        }

        private static string Short(string sha) =>
            string.IsNullOrEmpty(sha) ? "(none)" : (sha.Length > 12 ? sha.Substring(0, 12) + "…" : sha);
    }
}
