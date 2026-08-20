// -----------------------------------------------------------------------------
// Horizun Core tests - the permission ladder, decided by EFFECT, for every rung.
//
// THE DEFECT. read_only and safe_write were written as lists of things to refuse,
// and both lists were assembled before ExternalSideEffect existed as a
// classification anyone consulted. So a tool that declared "I reach outside the
// model" was admitted by the two profiles whose entire purpose is to forbid
// that. horizun_excel_write_rows is the one that shows how bad it reads: a
// machine set to read_only would refuse to move a wall and then rewrite a
// workbook on disk.
//
// The fix is not a longer list - a list is what failed. Each rung is decided by
// ToolEffect, and this file asserts the WHOLE matrix: every profile against
// every value of the enum. A new effect that nobody classified fails
// Every_effect_is_classified_by_every_profile instead of quietly inheriting
// whichever branch happens not to mention it.
//
// The matrix is deliberately driven by SYNTHETIC contracts. A test built from
// real tool names proves what those tools do today and silently stops covering
// the rung the moment somebody renames one; the name-based special cases are
// asserted separately, below, where they belong.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class SettingsEffectMatrixTests
    {
        private const string ReadOnly = "read_only";
        private const string SafeWrite = "safe_write";
        private const string FullWrite = "full_write";
        private const string UnsafeCode = "unsafe_code";

        private static readonly string[] AllProfiles = { ReadOnly, SafeWrite, FullWrite, UnsafeCode };

        /// <summary>
        /// THE LADDER, as data. Each rung lists exactly the effects it admits; everything
        /// else is refused. Cumulative by construction - each profile repeats what the one
        /// below it allows - because a ladder written as four independent lists is how the
        /// rungs came to disagree in the first place.
        /// </summary>
        private static readonly Dictionary<string, ToolEffect[]> Admits =
            new Dictionary<string, ToolEffect[]>(StringComparer.Ordinal)
            {
                // Reads, and steering which Revit/view the reads come from. Nothing that
                // changes the model, opens or closes a session, or writes a file.
                [ReadOnly] = new[] { ToolEffect.ReadOnly, ToolEffect.HostState },

                // The above, plus typed writes INSIDE the document. Still nothing written
                // outside it, and still no session change.
                [SafeWrite] = new[]
                {
                    ToolEffect.ReadOnly, ToolEffect.HostState,
                    ToolEffect.Mutating, ToolEffect.MutatingUnlessDryRun
                },

                // The above, plus document sessions and typed external writes.
                [FullWrite] = new[]
                {
                    ToolEffect.ReadOnly, ToolEffect.HostState,
                    ToolEffect.Mutating, ToolEffect.MutatingUnlessDryRun,
                    ToolEffect.DocumentSession, ToolEffect.ExternalSideEffect
                },

                // The above, plus horizun_execute_python - which is gated by NAME, not by
                // effect, and is asserted separately. This rung is explicit-only.
                [UnsafeCode] = new[]
                {
                    ToolEffect.ReadOnly, ToolEffect.HostState,
                    ToolEffect.Mutating, ToolEffect.MutatingUnlessDryRun,
                    ToolEffect.DocumentSession, ToolEffect.ExternalSideEffect
                }
            };

        /// <summary>
        /// Every profile answers for every effect, and answers what the ladder says.
        /// Enumerating the enum rather than listing cases is the point: this is the test
        /// that fails when a sixth effect is added and nobody decides what read_only
        /// thinks of it.
        /// </summary>
        [Fact]
        public void Every_effect_is_classified_by_every_profile()
        {
            var effects = (ToolEffect[])Enum.GetValues(typeof(ToolEffect));
            Assert.NotEmpty(effects);

            foreach (string profile in AllProfiles)
            {
                ToolEffect[] admitted = Admits[profile];
                foreach (ToolEffect effect in effects)
                {
                    bool expected = Array.IndexOf(admitted, effect) >= 0;
                    bool actual = AllowsEffect(profile, effect, out string reason);

                    Assert.True(expected == actual,
                        "permission_profile=" + profile + " with ToolEffect." + effect +
                        ": expected " + (expected ? "ALLOWED" : "REFUSED") +
                        " but got " + (actual ? "ALLOWED" : "REFUSED") +
                        ". Reason given: " + (reason ?? "(none)") +
                        ". If a new effect was added, decide what every rung of the ladder thinks of it " +
                        "in Settings.IsToolAllowed and record it in this matrix.");
                }
            }
        }

        /// <summary>
        /// The ladder only ever grows. Stated as its own property so a change that
        /// accidentally lets safe_write do something full_write refuses is a failure here,
        /// not a puzzle in the matrix above.
        /// </summary>
        [Fact]
        public void The_ladder_is_cumulative()
        {
            var order = new[] { ReadOnly, SafeWrite, FullWrite, UnsafeCode };
            var effects = (ToolEffect[])Enum.GetValues(typeof(ToolEffect));

            for (int i = 1; i < order.Length; i++)
                foreach (ToolEffect effect in effects)
                    if (AllowsEffect(order[i - 1], effect, out _))
                        Assert.True(AllowsEffect(order[i], effect, out _),
                            order[i] + " refuses ToolEffect." + effect + " while the lower rung " +
                            order[i - 1] + " allows it. The ladder must only ever grow.");
        }

        // ---------------------------------------------------------------------
        // The named tools. The matrix above proves the RULE; these prove the rule
        // reaches the tools whose misclassification is what started this.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The reported defect, as a test. It writes a file on disk; the two profiles that
        /// forbid writing outside the model must refuse it.
        /// </summary>
        [Theory]
        [InlineData(ReadOnly)]
        [InlineData(SafeWrite)]
        public void Excel_write_rows_is_refused_by_the_restrictive_profiles(string profile)
        {
            WithProfile(profile, () =>
            {
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_excel_write_rows"), out string reason));
                Assert.False(string.IsNullOrWhiteSpace(reason));
            });
        }

        [Theory]
        [InlineData(FullWrite)]
        [InlineData(UnsafeCode)]
        public void Excel_write_rows_is_allowed_where_external_writes_are_authorized(string profile)
        {
            WithProfile(profile, () =>
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_excel_write_rows"), out _)));
        }

        /// <summary>Capturing a view writes a PNG. Same rung as the workbook.</summary>
        [Theory]
        [InlineData(ReadOnly)]
        [InlineData(SafeWrite)]
        public void Capture_view_is_refused_by_the_restrictive_profiles(string profile)
        {
            WithProfile(profile, () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_capture_view"), out _)));
        }

        /// <summary>
        /// AND THE OTHER DIRECTION, which matters just as much. horizun_target chooses
        /// WHICH Revit every later call talks to and is answered without touching Revit at
        /// all; horizun_navigate hands query results back to the UI. Neither changes the
        /// model, opens a session or writes a file. They were sharing a classification with
        /// the workbook writer, so refusing everything "external" under read_only would
        /// have left a read-only machine unable to choose which Revit it was reading from -
        /// a fix that breaks the profile it was meant to protect.
        /// </summary>
        [Fact]
        public void Steering_the_host_stays_available_to_a_read_only_machine()
        {
            WithProfile(ReadOnly, () =>
            {
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_target"), out string t), t);
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_navigate"), out string n), n);
            });
        }

        [Fact]
        public void Reading_the_model_stays_available_to_a_read_only_machine()
        {
            WithProfile(ReadOnly, () =>
            {
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_health"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_query_model"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_list_elements"), out _));
            });
        }

        /// <summary>
        /// THE PRODUCT DECISION, pinned. execute_python is gated by name and needs BOTH
        /// unsafe_code and enable_execute_python are both explicit owner decisions. A
        /// fresh machine must not expose arbitrary code.
        /// </summary>
        [Fact]
        public void Execute_python_is_unavailable_on_a_default_install()
        {
            WithoutSettingsFile(() =>
            {
                Assert.Equal(SafeWrite, Settings.PermissionProfile);
                Assert.False(Settings.ExecutePythonEnabled);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
            });
        }

        /// <summary>...and only unsafe_code carries it, which is the other half of the same rule.</summary>
        [Theory]
        [InlineData(ReadOnly)]
        [InlineData(SafeWrite)]
        [InlineData(FullWrite)]
        public void Execute_python_is_refused_below_unsafe_code(string profile)
        {
            WithProfile(profile, () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
        }

        /// <summary>An explicit owner refusal is respected even at the default profile.</summary>
        [Fact]
        public void An_explicit_refusal_of_python_is_respected_at_unsafe_code()
        {
            WithSettings(@"{""permission_profile"":""unsafe_code"",""enable_execute_python"":false}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
        }

        // ---------------------------------------------------------------------

        /// <summary>
        /// Ask the ladder about an effect using a contract that is nothing but that effect.
        /// A synthetic name keeps the name-based special cases out of the answer, so this
        /// measures the rung and not the tool.
        /// </summary>
        private static bool AllowsEffect(string profile, ToolEffect effect, out string reason)
        {
            var contract = new CommandContract
            {
                Name = "horizun_test_synthetic_" + effect.ToString().ToLowerInvariant(),
                Command = "horizun_test_synthetic",
                Description = "Synthetic contract used to probe one rung of the permission ladder.",
                Effect = effect
            };

            string captured = null;
            bool allowed = false;
            WithProfile(profile, () => { allowed = Settings.IsToolAllowed(contract, out captured); });
            reason = captured;
            return allowed;
        }

        private static void WithProfile(string profile, Action action)
            => WithSettings(@"{""permission_profile"":""" + profile + @"""}", action);

        private static void WithSettings(string json, Action action)
        {
            using (new EnvGuard())
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                     "hz-effect-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    File.WriteAllText(HorizunPaths.SettingsPath(), json);
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }

        private static void WithoutSettingsFile(Action action)
        {
            using (new EnvGuard())
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                     "hz-effect-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);   // the root exists; settings.json does not
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
    }
}
