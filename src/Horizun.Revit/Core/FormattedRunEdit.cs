using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    // Offsets are UTF-16 positions, as in FormattedText and System.String.
    public static class FormattedRunEdit
    {
        public sealed class Run
        {
            public int Start, Length;
            public string Format;
            public Run(int start, int length, string format) { Start = start; Length = length; Format = format; }
        }
        public sealed class Edit { public int Start, Length; public string Format; }
        public sealed class Plan
        {
            public string Text;
            public List<Edit> Edits = new List<Edit>();
            public List<Run> Runs = new List<Run>();
        }
        // Compare per position, so equivalent adjacent runs need not have identical boundaries.
        public static List<string> Compare(IList<Run> expected, IList<Run> actual)
        {
            var differences = new List<string>();
            var a = expected.SelectMany(r => Enumerable.Repeat(r.Format, r.Length)).ToList();
            var b = actual.SelectMany(r => Enumerable.Repeat(r.Format, r.Length)).ToList();
            if (a.Count != b.Count) differences.Add("length: expected " + a.Count + ", actual " + b.Count);
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                if (a[i] != b[i]) differences.Add("position " + i + ": expected " + a[i] + ", actual " + b[i]);
            return differences;
        }
        public static Plan Create(string text, IList<Run> runs, string match, string replacement)
        {
            if (text == null || runs == null || string.IsNullOrEmpty(match) || replacement == null) throw new ArgumentException("Invalid replacement input.");
            int cursor = 0;
            foreach (Run r in runs)
            {
                if (r.Start != cursor || r.Length <= 0 || r.Format == null) throw new ArgumentException("Runs must cover the text in order.");
                cursor += r.Length;
            }
            if (cursor != text.Length) throw new ArgumentException("Runs must cover the text in order.");
            var chars = runs.SelectMany(r => Enumerable.Repeat(r.Format, r.Length)).ToList();
            var result = new Plan { Text = text };
            int from = 0;
            while ((from = result.Text.IndexOf(match, from, StringComparison.Ordinal)) >= 0)
            {
                string format = chars[from];
                if (chars.Skip(from).Take(match.Length).Any(f => f != format))
                    throw new ArgumentException("la coincidencia cruza tramos con formato distinto: " + string.Join(", ", runs.Select(r => r.Start + ":" + r.Length + ":" + r.Format)));
                result.Edits.Add(new Edit { Start = from, Length = match.Length, Format = format });
                result.Text = result.Text.Remove(from, match.Length).Insert(from, replacement);
                chars.RemoveRange(from, match.Length);
                chars.InsertRange(from, Enumerable.Repeat(format, replacement.Length));
                from += replacement.Length;
            }
            for (int i = 0; i < chars.Count; i++)
                if (result.Runs.Count > 0 && result.Runs[result.Runs.Count - 1].Format == chars[i]) result.Runs[result.Runs.Count - 1].Length++;
                else result.Runs.Add(new Run(i, 1, chars[i]));
            return result;
        }
    }
}
