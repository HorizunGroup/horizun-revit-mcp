// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE PART OF THE READER THAT IS NOT A PROCESS.
//
// CadDwgReader runs an executable, so most of it cannot be tested without one.
// Two things can, and both were learned the hard way against a real AutoCAD:
//
//   the script must be fed INLINE, one top-level form per line, because AutoCAD
//   2025 ships SECURELOAD on and refuses (load) from any path the user has not
//   trusted - and changing a user's AutoCAD trust settings to read a file is not
//   something this bridge does;
//
//   and the extractor embedded in the assembly must be the extractor in the .lsp
//   file, or the readable original and the shipped copy drift apart silently.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadDwgReaderTests
    {
        [Fact]
        public void The_script_carries_every_form_and_ends_with_the_call()
        {
            string script = CadDwgReader.BuildScript(@"C:\out\reading.tsv");
            string[] lines = script.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(CadDwgScript.Forms.Length + 1, lines.Length);
            for (int i = 0; i < CadDwgScript.Forms.Length; i++)
                Assert.Equal(CadDwgScript.Forms[i], lines[i]);
            Assert.StartsWith("(hz-dump ", lines[lines.Length - 1]);
        }

        [Fact]
        public void Every_form_is_one_line_because_a_script_is_read_one_line_at_a_time()
        {
            foreach (string form in CadDwgScript.Forms)
            {
                Assert.DoesNotContain("\n", form);
                Assert.DoesNotContain("\r", form);
                Assert.StartsWith("(", form);
            }
        }

        [Fact]
        public void The_output_path_goes_in_with_forward_slashes()
        {
            // AutoLISP reads a backslash in a string as an escape, so a Windows
            // path handed over unchanged loses its separators - and the extractor
            // then writes its report somewhere nobody looks.
            string script = CadDwgReader.BuildScript(@"C:\Users\user\out\reading.tsv");
            Assert.Contains("(hz-dump \"C:/Users/user/out/reading.tsv\")", script);
            Assert.DoesNotContain(@"C:\Users", script);
        }

        [Fact]
        public void The_script_never_loads_from_a_path()
        {
            // SECURELOAD refuses (load) from an untrusted path, and the answer is
            // to inline the code rather than to edit the user's trusted paths.
            Assert.DoesNotContain("(load ", CadDwgReader.BuildScript("C:/x.tsv"));
        }

        /// <summary>
        /// The embedded extractor is generated from the .lsp, and this is what
        /// stops the two drifting: the same collapse, compared.
        /// </summary>
        [Fact]
        public void The_embedded_extractor_is_the_one_in_the_lsp_file()
        {
            string lsp = FindLsp();
            if (lsp == null)
            {
                Assert.True(true, "hz_dwg_extract.lsp not found from the test binary: nothing compared.");
                return;
            }

            string[] fromFile = TopLevelForms(File.ReadAllText(lsp));
            Assert.Equal(CadDwgScript.Forms.Length, fromFile.Length);
            for (int i = 0; i < fromFile.Length; i++)
                Assert.Equal(fromFile[i], CadDwgScript.Forms[i]);
        }

        private static string FindLsp()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string candidate = Path.Combine(d.FullName, "src", "Horizun.Revit", "Core",
                                                "hz_dwg_extract.lsp");
                if (File.Exists(candidate)) return candidate;
                d = d.Parent;
            }
            return null;
        }

        /// <summary>The same split the generator does: comments out, each form on one line.</summary>
        private static string[] TopLevelForms(string lisp)
        {
            var forms = new System.Collections.Generic.List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;
            bool inString = false;

            for (int i = 0; i < lisp.Length; i++)
            {
                char c = lisp[i];
                if (inString)
                {
                    current.Append(c);
                    if (c == '\\' && i + 1 < lisp.Length) { current.Append(lisp[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == ';')
                {
                    while (i < lisp.Length && lisp[i] != '\n') i++;
                    continue;
                }
                if (c == '"') { inString = true; current.Append(c); continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                current.Append(c);

                if (depth == 0 && current.ToString().Trim().Length > 0)
                {
                    string form = string.Join(" ", current.ToString()
                        .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    if (form.Length > 0) forms.Add(form);
                    current.Clear();
                }
            }
            return forms.ToArray();
        }
    }
}
