// -----------------------------------------------------------------------------
// Horizun Revit MCP - local diagnostic history and transparent health roll-up.
// Nothing here writes a Revit document. Snapshot persistence is explicit and
// stays under %USERPROFILE%\.horizun; a health number exists only under a
// caller-supplied, versioned weighting profile.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class AuditDiagnosticArtifacts
    {
        public static JObject Health(JObject rawProfile, JArray findings, JArray checksFailed)
        {
            if (rawProfile == null)
                return new JObject
                {
                    ["status"] = "not_requested", ["score"] = JValue.CreateNull(),
                    ["why"] = "no health_profile was supplied. Weights are an organisation's opinion, so none is compiled in."
                };

            HealthProfile profile;
            string parseFailure = ReadProfile(rawProfile, out profile);
            List<string> codes = new List<string>();
            string refusal = parseFailure ?? HealthIndexRules.ValidateProfile(profile, out codes);
            if (refusal != null)
                return new JObject
                {
                    ["status"] = "refused", ["score"] = JValue.CreateNull(), ["why"] = refusal,
                    ["codes"] = new JArray(codes.Select(x => (JToken)x))
                };

            var byCheck = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (JObject finding in (findings ?? new JArray()).Children<JObject>())
            {
                string check = finding.Value<string>("check");
                if (!string.IsNullOrWhiteSpace(check)) byCheck[check] = finding;
            }
            var failed = new HashSet<string>((checksFailed ?? new JArray()).Children<JObject>()
                .Select(x => x.Value<string>("check")).Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.Ordinal);

            var dimensions = new List<HealthDimension>();
            foreach (HealthWeight weight in profile.Weights)
            {
                JObject finding;
                bool found = byCheck.TryGetValue(weight.Dimension, out finding);
                bool assessable = found && !failed.Contains(weight.Dimension);
                bool complete = assessable && (finding["coverage_complete"] == null ||
                                                finding.Value<bool>("coverage_complete"));
                var deductions = new List<HealthDeduction>();
                if (assessable && finding.Value<bool?>("is_issue") == true)
                {
                    double? count = Number(finding["count"]);
                    deductions.Add(new HealthDeduction
                    {
                        Check = weight.Dimension, Points = 100,
                        Why = weight.Dimension + " is an active finding" +
                              (count.HasValue ? " (count " + count.Value.ToString("0.###", CultureInfo.InvariantCulture) + ")" : "") +
                              ". This profile uses binary finding presence inside each weighted dimension."
                    });
                }
                dimensions.Add(HealthIndexRules.ScoreDimension(weight.Dimension, deductions,
                    applicable: true, assessable: assessable, coverageComplete: complete,
                    weight: weight.Weight, critical: weight.Critical));
            }

            HealthIndex index = HealthIndexRules.Roll(profile, dimensions);
            return HealthJson(index, dimensions);
        }

        public static void SnapshotAndTrend(bool requested, string modelFingerprint, string title,
                                            string revitVersion, string revitBuild, JObject requirementSet,
                                            double? fileSizeMb, long elementCount, long elementTypeCount,
                                            JArray findings, JArray checksFailed, bool coverageComplete,
                                            long durationMs, out JObject snapshotJson, out JObject trendJson)
        {
            if (!requested)
            {
                snapshotJson = new JObject { ["status"] = "not_requested" };
                trendJson = new JObject { ["status"] = "not_requested" };
                return;
            }
            if (string.IsNullOrWhiteSpace(modelFingerprint))
            {
                snapshotJson = new JObject
                {
                    ["status"] = "refused",
                    ["why"] = "the model has no stable fingerprint, so a saved measurement could not be shown to belong to it."
                };
                trendJson = new JObject { ["status"] = "not_comparable", ["why"] = snapshotJson["why"] };
                return;
            }

            DiagnosticsSnapshot now = BuildSnapshot(modelFingerprint, title, revitVersion, revitBuild,
                requirementSet, fileSizeMb, elementCount, elementTypeCount, findings, checksFailed,
                coverageComplete, durationMs);
            string directory = SnapshotStore.DirectoryUnder(HorizunPaths.DataRoot());
            string fileName = SafeFileName(modelFingerprint) + ".audit.json";
            string path = Path.Combine(directory, fileName);

            SnapshotReadResult previous;
            try { previous = SnapshotStore.Read(File.Exists(path) ? File.ReadAllText(path) : null); }
            catch (Exception ex)
            {
                previous = new SnapshotReadResult { Ok = false, Code = SnapshotStoreCodes.Unreadable, Message = ex.Message };
            }

            SnapshotComparison comparison = null;
            if (previous.Ok)
            {
                try { comparison = SnapshotRules.Compare(previous.Content.ToObject<DiagnosticsSnapshot>(), now); }
                catch (Exception ex)
                {
                    comparison = new SnapshotComparison { Comparable = false, WhyNot = ex.Message,
                        RefusalKind = SnapshotChangeKind.NotComparable };
                }
            }

            JObject content = JObject.FromObject(now);
            SnapshotWriteResult written = SnapshotStore.Write(directory, fileName, content, AtomicWrite);
            snapshotJson = new JObject
            {
                ["status"] = written.Ok ? "ok" : "failed",
                ["document_fingerprint"] = modelFingerprint,
                ["taken_utc"] = now.TakenUtc,
                ["coverage_complete"] = now.CoverageComplete,
                ["checks"] = now.Checks.Count,
                ["stored"] = written.Ok,
                ["file_name"] = fileName,
                ["sha256"] = written.Sha256,
                ["redacted_values"] = written.RedactedValues,
                ["why"] = written.Message
            };

            if (comparison != null)
            {
                trendJson = ComparisonJson(comparison);
            }
            else
            {
                trendJson = new JObject
                {
                    ["status"] = previous.Code == SnapshotStoreCodes.NotFound ? "no_baseline" : "not_comparable",
                    ["why"] = previous.Message, ["no_drift"] = JValue.CreateNull()
                };
            }
        }

        private static DiagnosticsSnapshot BuildSnapshot(string fingerprint, string title, string revitVersion,
            string revitBuild, JObject requirementSet, double? fileSizeMb, long elementCount, long elementTypeCount,
            JArray findings, JArray checksFailed, bool coverageComplete, long durationMs)
        {
            var snapshot = new DiagnosticsSnapshot
            {
                ModelFingerprint = fingerprint, DocumentTitle = title,
                RevitVersion = revitVersion, RevitBuild = revitBuild,
                HorizunCommit = Build.Commit, ScanVersion = Build.Version,
                RequirementSetSha256 = requirementSet == null ? null :
                    SnapshotStore.Sha256Of(SnapshotStore.Canonical(requirementSet)),
                TakenUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                FileSizeMb = fileSizeMb, ElementCount = elementCount, ElementTypeCount = elementTypeCount,
                DurationMs = durationMs, CoverageComplete = coverageComplete
            };
            foreach (JObject finding in (findings ?? new JArray()).Children<JObject>())
            {
                string check = finding.Value<string>("check");
                if (string.IsNullOrWhiteSpace(check)) continue;
                double? count = Number(finding["count"]);
                if (check == "warnings") snapshot.WarningCount = count.HasValue ? (long?)count.Value : null;
                snapshot.Checks.Add(new SnapshotCheck
                {
                    Check = check, IsIssue = finding.Value<bool?>("is_issue") == true, Count = count,
                    CoverageComplete = finding["coverage_complete"] == null || finding.Value<bool>("coverage_complete"),
                    CountIsLowerBound = finding["coverage_complete"] != null && !finding.Value<bool>("coverage_complete")
                });
            }
            foreach (JObject failed in (checksFailed ?? new JArray()).Children<JObject>())
            {
                string check = failed.Value<string>("check");
                if (string.IsNullOrWhiteSpace(check) || snapshot.Checks.Any(x => x.Check == check)) continue;
                snapshot.Checks.Add(new SnapshotCheck
                {
                    Check = check, Count = null, CoverageComplete = false, CountIsLowerBound = true
                });
            }
            return snapshot;
        }

        private static string ReadProfile(JObject raw, out HealthProfile profile)
        {
            profile = new HealthProfile
            {
                Id = raw.Value<string>("id"), Version = raw.Value<string>("version"),
                Context = raw.Value<string>("context") ?? HealthContext.Project
            };
            if (string.IsNullOrWhiteSpace(profile.Id)) return "health_profile.id must be a non-empty string.";
            if (string.IsNullOrWhiteSpace(profile.Version)) return "health_profile.version must be a non-empty string.";
            JArray weights = raw["weights"] as JArray;
            if (weights == null) return "health_profile.weights must be an array.";
            foreach (JObject row in weights.Children<JObject>())
                profile.Weights.Add(new HealthWeight
                {
                    Dimension = row.Value<string>("dimension"),
                    Weight = row.Value<double?>("weight") ?? double.NaN,
                    Critical = row.Value<bool?>("critical") == true
                });
            return null;
        }

        private static JObject HealthJson(HealthIndex index, IEnumerable<HealthDimension> dimensions)
        {
            var rows = new JArray();
            foreach (HealthDimension dimension in index.Dimensions)
            {
                var deductions = new JArray();
                foreach (HealthDeduction deduction in dimension.Deductions)
                    deductions.Add(new JObject
                    {
                        ["check"] = deduction.Check,
                        ["points"] = deduction.Points,
                        ["why"] = deduction.Why
                    });
                rows.Add(new JObject
                {
                    ["dimension"] = dimension.Dimension,
                    ["state"] = dimension.State,
                    ["score"] = dimension.Score.HasValue ? (JToken)dimension.Score.Value : JValue.CreateNull(),
                    ["weight"] = dimension.Weight,
                    ["critical"] = dimension.Critical,
                    ["coverage_complete"] = dimension.CoverageComplete,
                    ["why"] = dimension.Why,
                    ["deductions"] = deductions
                });
            }
            return new JObject
            {
                ["status"] = "ok",
                ["profile_id"] = index.ProfileId,
                ["profile_version"] = index.ProfileVersion,
                ["context"] = index.Context,
                ["score"] = index.Score.HasValue ? (JToken)index.Score.Value : JValue.CreateNull(),
                ["score_suppressed_because"] = index.ScoreSuppressedBecause,
                ["assessed_weight_share"] = index.AssessedWeightShare.HasValue
                    ? (JToken)index.AssessedWeightShare.Value : JValue.CreateNull(),
                ["plausible_low"] = index.PlausibleLow.HasValue ? (JToken)index.PlausibleLow.Value : JValue.CreateNull(),
                ["plausible_high"] = index.PlausibleHigh.HasValue ? (JToken)index.PlausibleHigh.Value : JValue.CreateNull(),
                ["unassessed"] = new JArray(index.Unassessed.Select(x => (JToken)x)),
                ["dimensions"] = rows,
                ["scoring_method"] = "binary_finding_presence",
                ["coverage_complete"] = index.Unassessed.Count == 0 && dimensions.All(x => x.CoverageComplete),
                ["means"] = HealthIndexRules.Means +
                    " Inside each declared dimension this audit uses binary finding presence: 100 when that " +
                    "finding is absent, 0 when it is active. The profile controls only the visible weights."
            };
        }

        private static JObject ComparisonJson(SnapshotComparison comparison)
        {
            var changes = new JArray();
            foreach (SnapshotChange change in comparison.Changes)
                changes.Add(new JObject
                {
                    ["check"] = change.Check,
                    ["kind"] = change.Kind,
                    ["before"] = change.Before.HasValue ? (JToken)change.Before.Value : JValue.CreateNull(),
                    ["after"] = change.After.HasValue ? (JToken)change.After.Value : JValue.CreateNull(),
                    ["delta"] = change.Delta.HasValue ? (JToken)change.Delta.Value : JValue.CreateNull(),
                    ["why"] = change.Why
                });
            bool noDrift = comparison.Comparable && comparison.New == 0 && comparison.Resolved == 0 &&
                           comparison.Worsened == 0 && comparison.Improved == 0 &&
                           comparison.CoverageChanged == 0 && comparison.NotComparable == 0;
            return new JObject
            {
                ["status"] = comparison.Comparable ? "ok" : "not_comparable",
                ["comparable"] = comparison.Comparable,
                ["why_not"] = comparison.WhyNot,
                ["refusal_kind"] = comparison.RefusalKind,
                ["from_utc"] = comparison.FromUtc,
                ["to_utc"] = comparison.ToUtc,
                ["new"] = comparison.New,
                ["resolved"] = comparison.Resolved,
                ["persistent"] = comparison.Persistent,
                ["worsened"] = comparison.Worsened,
                ["improved"] = comparison.Improved,
                ["coverage_changed"] = comparison.CoverageChanged,
                ["not_comparable"] = comparison.NotComparable,
                ["changes"] = changes,
                ["no_drift"] = comparison.Comparable ? (JToken)noDrift : JValue.CreateNull()
            };
        }

        private static double? Number(JToken token)
        {
            return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                ? token.Value<double>() : (double?)null;
        }

        private static string SafeFileName(string value)
        {
            return new string((value ?? "model").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        }

        private static bool AtomicWrite(string path, string text)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temp, text);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }
    }
}
