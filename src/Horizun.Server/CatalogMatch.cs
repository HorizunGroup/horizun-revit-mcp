// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// horizun_catalog_lookup operation=match: pair a model's types with catalog codes
// and EXPLAIN every pairing, so a person (or a threshold) can accept or reject it.
//
// operation=search answers "which descriptions share words with this text". That
// is not enough to code a model: a 15 cm wall and a 20 cm wall share every word,
// and the one that ranks first is a coin toss. Here each candidate carries:
//   * confidence (0-100): how much of the query the candidate covers (recall,
//     weighted 0.7) and how much of the candidate the query explains (precision,
//     0.3); numbers weigh 1.5 words because they are what tells sizes apart;
//   * matched / missing features, and number_conflicts (a size the candidate has
//     and the query contradicts), which cut the confidence by 30 %;
//   * a decision: accept only when the best candidate reaches the threshold AND is
//     at least 5 points clear of the next; otherwise review, with the reason.
// The method is deterministic and stated in the reply; it is a ranking aid, not a
// classifier, and nothing is written - accepted pairs go to horizun_set_keynote.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class CatalogMatch
    {
        private const double NumberWeight = 1.5;
        private const double MinMargin = 5.0;
        private const double ConflictFactor = 0.7;

        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.Ordinal)
        {
            "de", "del", "la", "las", "el", "los", "y", "e", "en", "con", "para", "por", "a", "al", "un", "una",
            "o", "u", "of", "the", "and", "with", "for", "in", "on", "to", "tipo", "type", "familia", "family", "hz"
        };

        private static readonly Dictionary<string, double> UnitToMm = new Dictionary<string, double>(StringComparer.Ordinal)
        { { "mm", 1 }, { "cm", 10 }, { "m", 1000 } };

        internal sealed class Features
        {
            public readonly List<string> Words = new List<string>();
            public readonly List<Num> Numbers = new List<Num>();
        }

        internal struct Num
        {
            public double Value;     // as written
            public double? Mm;       // when a unit followed it
            public string Text;
        }

        internal static Features Extract(string text)
        {
            var f = new Features();
            var tokens = Split(TextFold.Fold(text ?? ""));
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    double? mm = null;
                    string label = t;
                    if (i + 1 < tokens.Count && UnitToMm.TryGetValue(tokens[i + 1], out double k))
                    { mm = v * k; label = t + " " + tokens[i + 1]; i++; }
                    f.Numbers.Add(new Num { Value = v, Mm = mm, Text = label });
                    continue;
                }
                // "150mm" glued together
                int cut = 0;
                while (cut < t.Length && (char.IsDigit(t[cut]) || t[cut] == '.' || t[cut] == ',')) cut++;
                if (cut > 0 && cut < t.Length && UnitToMm.TryGetValue(t.Substring(cut), out double k2) &&
                    double.TryParse(t.Substring(0, cut).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v2))
                {
                    f.Numbers.Add(new Num { Value = v2, Mm = v2 * k2, Text = t });
                    continue;
                }
                if (t.Length < 2 || Stop.Contains(t) || UnitToMm.ContainsKey(t)) continue;
                string w = Stem(t);
                if (!f.Words.Contains(w)) f.Words.Add(w);
            }
            return f;
        }

        private static List<string> Split(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool decimalMark = (c == '.' || c == ',') && sb.Length > 0 && char.IsDigit(sb[sb.Length - 1]) &&
                                   i + 1 < s.Length && char.IsDigit(s[i + 1]);
                if (char.IsLetterOrDigit(c) || decimalMark) { sb.Append(c); continue; }
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        private static string Stem(string w)
        {
            if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal)) return w.Substring(0, w.Length - 2);
            if (w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal)) return w.Substring(0, w.Length - 1);
            return w;
        }

        private static bool SameNumber(Num a, Num b)
        {
            if (a.Mm.HasValue && b.Mm.HasValue) return Math.Abs(a.Mm.Value - b.Mm.Value) < 0.01;
            if (Math.Abs(a.Value - b.Value) < 1e-9) return true;
            // one side without unit: compare its raw value against the other's millimetres
            if (a.Mm.HasValue && !b.Mm.HasValue) return Math.Abs(a.Mm.Value - b.Value) < 0.01;
            if (b.Mm.HasValue && !a.Mm.HasValue) return Math.Abs(b.Mm.Value - a.Value) < 0.01;
            return false;
        }

        internal sealed class Scored
        {
            public string Code, Description;
            public double Confidence;
            public List<string> Matched = new List<string>(), Missing = new List<string>(), Conflicts = new List<string>();
        }

        internal static Scored Score(Features q, Features d, string code, string description)
        {
            var s = new Scored { Code = code, Description = description };
            double hit = 0;
            foreach (string w in q.Words)
            {
                if (d.Words.Contains(w)) { hit += 1; s.Matched.Add(w); continue; }
                string partial = d.Words.FirstOrDefault(x => x.Length >= 5 && w.Length >= 5 &&
                    (x.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(x, StringComparison.Ordinal)));
                if (partial != null) { hit += 0.8; s.Matched.Add(w + "~" + partial); }
                else s.Missing.Add(w);
            }
            foreach (Num n in q.Numbers)
            {
                if (d.Numbers.Any(x => SameNumber(n, x))) { hit += NumberWeight; s.Matched.Add(n.Text); }
                else s.Missing.Add(n.Text);
            }
            if (q.Numbers.Count > 0)
                foreach (Num n in d.Numbers)
                    if (!q.Numbers.Any(x => SameNumber(x, n))) s.Conflicts.Add(n.Text);
            double qWeight = q.Words.Count + NumberWeight * q.Numbers.Count;
            double dWeight = d.Words.Count + NumberWeight * d.Numbers.Count;
            if (qWeight <= 0 || hit <= 0) { s.Confidence = 0; return s; }
            double recall = hit / qWeight;
            double precision = dWeight > 0 ? Math.Min(1.0, hit / dWeight) : 0;
            double c = 100.0 * (0.7 * recall + 0.3 * precision);
            if (s.Conflicts.Count > 0) c *= ConflictFactor;
            s.Confidence = Math.Round(c, 1);
            return s;
        }

        internal static JObject Handle(JObject args, CancellationToken cancellationToken)
        {
            string catalogPath = (string)args?["catalog_path"];
            string separator = (string)args?["separator"];
            string delimiterArg = (string)args?["delimiter"];
            bool hasHeader = args?["has_header"]?.Type == JTokenType.Boolean && (bool)args["has_header"];
            JToken codeColumn = args?["code_column"];
            JToken descriptionColumn = args?["description_column"];
            int perQuery = args?["max_results"] != null ? (int)args["max_results"] : 3;
            double threshold = args?["threshold"] != null ? (double)args["threshold"] : 80.0;
            bool leafOnly = args?["leaf_only"] == null || (bool)args["leaf_only"];

            CatalogLookup.ValidateCatalogPath(catalogPath);
            if (perQuery <= 0) throw new ArgumentException("max_results must be a positive integer.");
            if (perQuery > 20) perQuery = 20;
            if (threshold < 0 || threshold > 100) throw new ArgumentException("threshold must be between 0 and 100.");

            var queries = new List<KeyValuePair<JToken, string>>();
            if (args?["queries"] is JArray qa)
            {
                if (qa.Count > 500) throw new ArgumentException("queries is limited to 500 items per call.");
                foreach (JToken item in qa)
                {
                    string text = item?["text"]?.Type == JTokenType.String ? (string)item["text"] : null;
                    if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Every queries item needs a non-blank text.");
                    queries.Add(new KeyValuePair<JToken, string>(item["id"], text));
                }
            }
            if (!string.IsNullOrWhiteSpace((string)args?["query"]))
                queries.Add(new KeyValuePair<JToken, string>(null, (string)args["query"]));
            if (queries.Count == 0) throw new ArgumentException("match needs queries (array of {id, text}) or query.");

            if (!File.Exists(catalogPath)) throw new FileNotFoundException("Catalog file not found: " + catalogPath);
            CatalogLookup.ValidateCatalogSize(new FileInfo(catalogPath).Length, catalogPath);
            byte[] bytes = File.ReadAllBytes(catalogPath);
            string sha = CatalogLookup.Sha256Hex(bytes);
            string text0 = CatalogLookup.DecodeCatalog(CatalogLookup.StripUtf8Bom(bytes), out string encodingUsed);
            ColumnDelimiterDetection detection = CatalogLookup.DetectColumnDelimiter(text0, delimiterArg);
            CatalogRows parsed = CatalogLookup.ParseCatalogRows(text0, detection.Delimiter, codeColumn, descriptionColumn ?? new JValue(1), hasHeader);

            var allCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in parsed.Codes) allCodes.Add(row.Key);
            var rows = new List<Tuple<string, string, Features>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int skippedNonLeaf = 0;
            foreach (var row in parsed.Codes)
            {
                if (!seen.Add(row.Key)) continue;
                if (leafOnly && CatalogLookup.EvaluateLeaf(allCodes, row.Key, separator).IsLeaf != true) { skippedNonLeaf++; continue; }
                rows.Add(Tuple.Create(row.Key, row.Value, Extract(row.Value)));
            }

            var results = new JArray();
            int accepted = 0;
            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Features qf = Extract(query.Value);
                var scored = rows.Select(r => Score(qf, r.Item3, r.Item1, r.Item2))
                                 .Where(x => x.Confidence > 0)
                                 .OrderByDescending(x => x.Confidence).ThenBy(x => x.Code, StringComparer.Ordinal)
                                 .Take(perQuery).ToList();
                var best = scored.FirstOrDefault();
                var second = scored.Skip(1).FirstOrDefault();
                string decision, reason;
                if (best == null) { decision = "review"; reason = "no_candidate: no catalog description shares a word or size with this text."; }
                else if (best.Confidence < threshold)
                { decision = "review"; reason = "below_threshold: best candidate " + best.Code + " at " + Fmt(best.Confidence) + " % < " + Fmt(threshold) + " %."; }
                else if (second != null && best.Confidence - second.Confidence < MinMargin)
                { decision = "review"; reason = "ambiguous: " + best.Code + " (" + Fmt(best.Confidence) + " %) and " + second.Code + " (" + Fmt(second.Confidence) + " %) are less than " + Fmt(MinMargin) + " points apart."; }
                else { decision = "accept"; reason = best.Code + " reaches " + Fmt(best.Confidence) + " % (threshold " + Fmt(threshold) + " %)" + (second == null ? "." : " and leads the next by " + Fmt(best.Confidence - second.Confidence) + " points."); }
                if (decision == "accept") accepted++;

                var candidates = new JArray();
                foreach (var c in scored)
                {
                    var why = new List<string>();
                    if (c.Confidence < threshold) why.Add("below threshold");
                    if (c.Conflicts.Count > 0) why.Add("size conflict: " + string.Join(", ", c.Conflicts));
                    if (c.Missing.Count > 0) why.Add("missing: " + string.Join(", ", c.Missing));
                    if (c != best && best != null) why.Add("not the best candidate");
                    candidates.Add(new JObject
                    {
                        ["code"] = c.Code, ["description"] = c.Description, ["confidence"] = c.Confidence,
                        ["matched_features"] = new JArray(c.Matched), ["missing_features"] = new JArray(c.Missing),
                        ["number_conflicts"] = new JArray(c.Conflicts),
                        ["rejection_reason"] = decision == "accept" && c == best ? null : (why.Count == 0 ? null : string.Join("; ", why))
                    });
                }
                results.Add(new JObject
                {
                    ["id"] = query.Key, ["text"] = query.Value,
                    ["features"] = new JObject { ["words"] = new JArray(qf.Words), ["numbers"] = new JArray(qf.Numbers.Select(n => n.Text)) },
                    ["decision"] = decision, ["code"] = decision == "accept" ? best.Code : null,
                    ["confidence"] = best?.Confidence, ["reason"] = reason, ["candidates"] = candidates
                });
            }

            return new JObject
            {
                ["operation"] = "match", ["catalog_path"] = catalogPath, ["sha256"] = sha, ["encoding_used"] = encodingUsed,
                ["delimiter_used"] = detection.Delimiter, ["delimiter_mode"] = detection.Mode,
                ["candidate_codes"] = rows.Count, ["skipped_non_leaf"] = skippedNonLeaf, ["leaf_only"] = leafOnly,
                ["threshold"] = threshold, ["queries"] = queries.Count, ["accepted"] = accepted, ["review"] = queries.Count - accepted,
                ["results"] = results,
                ["method"] = "Accent/case-folded words (light plural stemming, stop words dropped) and sizes (mm/cm/m normalized to mm). " +
                             "confidence = 100 x (0.7 x recall + 0.3 x precision), numbers weigh 1.5 words, a size in the candidate that the " +
                             "query contradicts multiplies it by 0.7. accept = best >= threshold and >= 5 points ahead of the next. " +
                             "Deterministic ranking aid, not a classifier: review every 'review' item; nothing was written."
            };
        }

        private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
