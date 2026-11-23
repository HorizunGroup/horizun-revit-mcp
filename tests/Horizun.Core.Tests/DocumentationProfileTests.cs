// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// FIELD FEEDBACK 2.7 / 2.8 / §4.2 (2026-10-18). A user ran v0.6.0 on the
// default profile and could neither save nor print a PDF: both tools were hidden from
// safe_write by NAME. They are listed now, and each command narrows what the profile
// does not authorize PER CALL (OperationPermission). The new documentation profile
// admits by toolset MEMBERSHIP. These tests pin all three halves.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class DocumentationProfileTests
    {
        // ---- the save operation, per profile -----------------------------------------

        [Theory]
        [InlineData("read_only")]
        [InlineData("documentation")]
        [InlineData("safe_write")]
        [InlineData("full_write")]
        [InlineData("unsafe_code")]
        public void Save_local_and_relinquish_own_are_never_refused_by_the_operation_rule(string profile)
        {
            Assert.Null(OperationPermission.SaveRefusal(profile, "save_local"));
            Assert.Null(OperationPermission.SaveRefusal(profile, "relinquish_own"));
        }

        [Theory]
        [InlineData("read_only", false)]
        [InlineData("documentation", false)]
        [InlineData("safe_write", false)]
        [InlineData("full_write", true)]
        [InlineData("unsafe_code", true)]
        public void Sync_with_central_needs_full_write(string profile, bool allowed)
        {
            string refusal = OperationPermission.SaveRefusal(profile, "sync_with_central");
            Assert.Equal(allowed, refusal == null);
            if (!allowed)
            {
                Assert.Contains("permission_profile=" + profile, refusal);
                Assert.Contains("Nothing was synchronized", refusal);
            }
        }

        [Fact]
        public void An_unknown_save_operation_is_refused_not_defaulted()
        {
            Assert.NotNull(OperationPermission.SaveRefusal("full_write", "save_as"));
            Assert.NotNull(OperationPermission.SaveRefusal("full_write", null));
        }

        // ---- the export format, per profile -----------------------------------------

        [Theory]
        [InlineData("read_only")]
        [InlineData("documentation")]
        [InlineData("safe_write")]
        public void Restricted_profiles_export_pdf_and_image_only(string profile)
        {
            Assert.Null(OperationPermission.ExportRefusal(profile, "pdf"));
            Assert.Null(OperationPermission.ExportRefusal(profile, "PDF"));
            // A picture of one view writes nothing to the model (field report: refused on
            // safe_write, agents fell back to screenshots). ExportCommand still requires
            // exactly one view_id for it.
            Assert.Null(OperationPermission.ExportRefusal(profile, "image"));
            Assert.Null(OperationPermission.ExportRefusal(profile, "IMAGE"));
            foreach (string format in new[] { "dwg", "ifc", "nwc", "fbx", "cobie", "rfa", "schedule_csv", "gbxml", "dwg_layers" })
            {
                string refusal = OperationPermission.ExportRefusal(profile, format);
                Assert.NotNull(refusal);
                Assert.Contains("permission_profile=" + profile, refusal);
                Assert.Contains("Nothing was exported", refusal);
                Assert.Contains("format=image", refusal);
            }
        }

        [Theory]
        [InlineData("full_write")]
        [InlineData("unsafe_code")]
        public void Full_profiles_export_every_format(string profile)
        {
            foreach (string format in new[] { "pdf", "image", "dwg", "ifc", "nwc", "cobie" })
                Assert.Null(OperationPermission.ExportRefusal(profile, format));
        }

        // ---- the documentation profile's element kinds --------------------------------

        [Fact]
        public void Documentation_targets_pass_and_one_model_element_refuses_the_whole_batch()
        {
            var ok = new[]
            {
                new DocumentationTarget { Id = 1, Kind = DocumentationTarget.ViewKind },
                new DocumentationTarget { Id = 2, Kind = DocumentationTarget.ViewOwnedKind },
                new DocumentationTarget { Id = 3, Kind = DocumentationTarget.AnnotationKind }
            };
            Assert.Null(OperationPermission.DocumentationTargetRefusal("documentation", ok, "horizun_delete_verified"));

            var mixed = ok.Concat(new[]
            {
                new DocumentationTarget { Id = 44, Kind = DocumentationTarget.ModelKind, Category = "Walls" },
                new DocumentationTarget { Id = 55, Kind = DocumentationTarget.MissingKind }
            }).ToArray();
            string refusal = OperationPermission.DocumentationTargetRefusal("documentation", mixed, "horizun_delete_verified");
            Assert.NotNull(refusal);
            Assert.Contains("44 (model, Walls)", refusal);
            Assert.Contains("55 (missing)", refusal);
            Assert.Contains("2 target(s)", refusal);
            Assert.Contains("nothing was written", refusal);
            Assert.DoesNotContain("1 (view", refusal);

            // Other profiles are not narrowed by this rule.
            Assert.Null(OperationPermission.DocumentationTargetRefusal("safe_write", mixed, "horizun_delete_verified"));
        }

        // ---- admission by toolset membership -----------------------------------------

        [Fact]
        public void Documentation_admits_its_toolset_reads_and_the_named_extras()
        {
            WithProfile("documentation", () =>
            {
                Assert.Equal("documentation", Settings.PermissionProfile);
                foreach (string tool in new[]
                {
                    "horizun_health", "horizun_target", "horizun_query_model", "horizun_list_elements",
                    "horizun_manage_views", "horizun_plan_views", "horizun_annotate", "horizun_edit_dimensions",
                    "horizun_detail_2d", "horizun_manage_revisions", "horizun_pack_sheets", "horizun_capture_view",
                    "horizun_save_document", "horizun_export", "horizun_undo", "horizun_delete_verified",
                    "horizun_write_params_verified"
                })
                    Assert.True(Settings.IsToolAllowed(Contract.Find(tool), out string reason), tool + ": " + reason);
            });
        }

        [Fact]
        public void Documentation_hides_model_writes_sessions_externals_and_python()
        {
            WithSettings(@"{""permission_profile"":""documentation"",""execute_python_ui_granted"":true,""enable_execute_python"":true}", () =>
            {
                foreach (string tool in new[]
                {
                    "horizun_create_elements", "horizun_transform_elements", "horizun_execute_plan",
                    "horizun_manage_styles", "horizun_manage_phases", "horizun_document_session",
                    "horizun_open_document", "horizun_relinquish_all", "horizun_excel_write_rows",
                    "horizun_execute_python", "horizun_set_keynote"
                })
                {
                    Assert.False(Settings.IsToolAllowed(Contract.Find(tool), out string reason), tool);
                    Assert.Contains("permission_profile=documentation", reason ?? "");
                }
            });
        }

        /// <summary>
        /// THE MEMBERSHIP RULE ITSELF: every writing tool the catalog puts in the
        /// documentation toolset is admitted - except the two declared exclusions and
        /// anything whose effect opens a session or writes external artefacts - so a tool
        /// another change adds to that toolset needs no edit in Settings.
        /// </summary>
        [Fact]
        public void Every_documentation_toolset_member_is_admitted_except_the_exclusions()
        {
            WithProfile("documentation", () =>
            {
                foreach (string tool in ToolsetCatalog.MembersOf("documentation"))
                {
                    CommandContract c = Contract.Find(tool);
                    bool excluded = tool == "horizun_manage_styles" || tool == "horizun_manage_phases" ||
                                    (c.Effect == ToolEffect.DocumentSession && tool != "horizun_save_document") ||
                                    (c.Effect == ToolEffect.ExternalSideEffect && tool != "horizun_capture_view");
                    Assert.True(Settings.IsToolAllowed(c, out string reason) == !excluded, tool + ": " + reason);
                }
            });
        }

        [Fact]
        public void Documentation_is_selectable_from_revit_and_from_the_file()
        {
            WithProfile("safe_write", () =>
            {
                Assert.True(Settings.TrySetPermissionProfile("documentation", out string error), error);
                Assert.Equal("documentation", Settings.PermissionProfile);
                Assert.False(Settings.AllowsExternalSideEffect(out string reason));
                Assert.Contains("documentation", reason);
            });
        }

        [Fact]
        public void Save_document_and_export_ride_on_documentation_as_well_as_their_own_toolsets()
        {
            Assert.Contains("documentation", ToolsetCatalog.Of("horizun_save_document"));
            Assert.Contains("administration", ToolsetCatalog.Of("horizun_save_document"));
            Assert.Contains("documentation", ToolsetCatalog.Of("horizun_export"));
            Assert.Contains("interoperability", ToolsetCatalog.Of("horizun_export"));
        }

        private static void WithProfile(string profile, Action action)
            => WithSettings(@"{""permission_profile"":""" + profile + @"""}", action);

        private static void WithSettings(string json, Action action)
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-docprofile-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    File.WriteAllText(HorizunPaths.SettingsPath(), json);
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
    }

    public sealed class PdfNamePatternTests
    {
        private static readonly DateTime Oct18 = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Local);

        private static PdfNameSource Sheet(long id, string number, string name) =>
            new PdfNameSource { ViewId = id, ViewName = "Sheet: " + number + " - " + name, SheetNumber = number, SheetName = name };

        [Fact]
        public void The_requested_example_names_A101_20261018_v1()
        {
            string[] paths = PdfNamePattern.Paths(@"C:\out", "{sheet_number}_{date}_v{version}",
                new[] { Sheet(10, "A101", "Planta") }, combine: false, localNow: Oct18, version: 1);
            Assert.Equal(new[] { Path.Combine(@"C:\out", "A101_20261018_v1.pdf") }, paths);
        }

        [Fact]
        public void Per_file_names_follow_view_order_and_version_has_no_padding()
        {
            string[] paths = PdfNamePattern.Paths(@"C:\out", "{index}-{sheet_number} {sheet_name}_v{version}.pdf",
                new[] { Sheet(10, "A101", "Planta"), Sheet(11, "A102", "Cortes") }, false, Oct18, 12);
            Assert.Equal(new[] { "1-A101 Planta_v12.pdf", "2-A102 Cortes_v12.pdf" }, paths.Select(Path.GetFileName));
        }

        [Fact]
        public void Combined_export_is_named_by_the_first_view()
        {
            string[] paths = PdfNamePattern.Paths(@"C:\out", "{sheet_number}_set",
                new[] { Sheet(10, "A101", "Planta"), Sheet(11, "A102", "Cortes") }, true, Oct18, 1);
            Assert.Equal(new[] { "A101_set.pdf" }, paths.Select(Path.GetFileName));
        }

        [Fact]
        public void Invalid_file_name_characters_are_replaced()
        {
            string[] paths = PdfNamePattern.Paths(@"C:\out", "{sheet_number}_{sheet_name}",
                new[] { Sheet(10, "A/101", "Planta: nivel 1?") }, false, Oct18, 1);
            Assert.Equal("A_101_Planta_ nivel 1_.pdf", Path.GetFileName(paths[0]));
            Assert.Equal("_CON.x", PdfNamePattern.Sanitize("CON.x"));
            Assert.Equal("abc", PdfNamePattern.Sanitize(" abc. "));
        }

        [Fact]
        public void Duplicate_resulting_names_refuse_naming_the_collision()
        {
            var ex = Assert.Throws<ArgumentException>(() => PdfNamePattern.Paths(@"C:\out", "entrega_{date}",
                new[] { Sheet(10, "A101", "x"), Sheet(11, "A102", "y") }, false, Oct18, 1));
            Assert.Contains("entrega_20261018.pdf", ex.Message);
            // Case-insensitive, as Windows is.
            Assert.Throws<ArgumentException>(() => PdfNamePattern.Paths(@"C:\out", "{sheet_number}",
                new[] { Sheet(10, "a101", "x"), Sheet(11, "A101", "y") }, false, Oct18, 1));
        }

        [Fact]
        public void Unknown_tokens_unbalanced_braces_and_sheet_tokens_on_views_refuse()
        {
            var views = new[] { Sheet(10, "A101", "x") };
            Assert.Contains("{revision}", Assert.Throws<ArgumentException>(() =>
                PdfNamePattern.Paths(@"C:\out", "{sheet_number}_{revision}", views, false, Oct18, 1)).Message);
            Assert.Throws<ArgumentException>(() => PdfNamePattern.Paths(@"C:\out", "{sheet_number", views, false, Oct18, 1));
            Assert.Throws<ArgumentException>(() => PdfNamePattern.Paths(@"C:\out", "sheet}", views, false, Oct18, 1));
            var plan = new[] { new PdfNameSource { ViewId = 77, ViewName = "Nivel 1" } };
            Assert.Contains("77", Assert.Throws<ArgumentException>(() =>
                PdfNamePattern.Paths(@"C:\out", "{sheet_number}", plan, false, Oct18, 1)).Message);
            Assert.Equal("Nivel 1.pdf", Path.GetFileName(PdfNamePattern.Paths(@"C:\out", "{view_name}", plan, false, Oct18, 1)[0]));
            Assert.Throws<ArgumentException>(() => PdfNamePattern.Paths(@"C:\out", "{sheet_number}", views, false, Oct18, -1));
            var unnamed = new[] { new PdfNameSource { ViewId = 78, ViewName = " . " } };
            Assert.Contains("empty", Assert.Throws<ArgumentException>(() =>
                PdfNamePattern.Paths(@"C:\out", "{view_name}", unnamed, false, Oct18, 1)).Message);
        }
    }
}
