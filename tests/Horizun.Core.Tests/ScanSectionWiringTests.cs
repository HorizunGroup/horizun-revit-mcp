// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE FOUR PLACES A SECTION HAS TO EXIST, checked against each other.
//
// A model_scan section is not one declaration. It is a name in AllSections, a
// Section(...) call that builds it, a method that does the building, and an
// entry in the tool's contract. Add three of the four and the failure is
// SILENT in the worst possible way:
//
//   missing from AllSections   the section never runs, and because a section
//                              nobody asked for reports "not_requested", the
//                              reply looks deliberate.
//   missing from the calls     the name is offered, accepted, and answers
//                              nothing.
//   missing from the contract  the schema rejects the very name the tool
//                              advertises, so the caller is told their request
//                              is invalid.
//
// None of those throw, and none of them show up in a test that only checks the
// rules. This file is the cross-check, and it reads the real sources rather
// than a list somebody remembered to update.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScanSectionWiringTests
    {
        private static DirectoryInfo Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir;
        }

        /// <summary>
        /// Source with comments blanked, offsets preserved.
        ///
        /// Every guard in this file asks whether the COMMAND does something, and a
        /// comment saying it must not do that thing is not the command doing it.
        /// Written the naive way, the guard forbidding Definition.ParameterGroup
        /// failed on the sentence explaining why ParameterGroup is forbidden.
        ///
        /// Quote, apostrophe and backslash are spelled as character codes: this is
        /// a routine about escaping, and writing its own delimiters as escapes
        /// makes it the hardest thing here to read correctly.
        /// </summary>
        private static string WithoutComments(string src)
        {
            const char Quote = (char)34;
            const char Apos = (char)39;
            const char Backslash = (char)92;
            const char Newline = (char)10;

            var sb = new System.Text.StringBuilder(src);
            bool inString = false, inChar = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inString)
                {
                    if (c == Backslash) { i++; continue; }
                    if (c == Quote) inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == Backslash) { i++; continue; }
                    if (c == Apos) inChar = false;
                    continue;
                }
                if (c == Quote) { inString = true; continue; }
                if (c == Apos) { inChar = true; continue; }

                if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != Newline) { sb[i] = ' '; i++; }
                    continue;
                }
                if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
                {
                    while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                    {
                        if (src[i] != Newline) sb[i] = ' ';
                        i++;
                    }
                    if (i + 1 < src.Length) { sb[i] = ' '; sb[i + 1] = ' '; i++; }
                    continue;
                }
            }
            return sb.ToString();
        }

        private static string Scan()
        {
            return File.ReadAllText(Path.Combine(Root().FullName,
                "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs"));
        }

        private static string Contract()
        {
            return File.ReadAllText(Path.Combine(Root().FullName,
                "src", "Horizun.Contracts", "Contract.cs"));
        }

        /// <summary>The names in AllSections, read from the source rather than restated here.</summary>
        private static List<string> DeclaredSections()
        {
            // Comments blanked first: a commented-out Section(...) call would
            // otherwise satisfy "every declared section is built".
            string src = WithoutComments(Scan());
            int i = src.IndexOf("AllSections", StringComparison.Ordinal);
            Assert.True(i >= 0, "AllSections not found");
            int open = src.IndexOf('{', i);
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            Assert.True(open > 0 && close > open, "AllSections body not found");

            return Regex.Matches(src.Substring(open, close - open), "\"([a-z_]+)\"")
                        .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>The sections the model_scan schema will actually accept.</summary>
        private static List<string> ContractSections()
        {
            string c = Contract();
            // Scoped to the one enum that lists model_scan's sections. Other tools
            // in this file have enums of their own, and a loose match would compare
            // this list against somebody else's.
            Match m = Regex.Match(c, @"""""enum"""": \[""""document"""",([^\]]*)\]");
            Assert.True(m.Success, "the model_scan sections enum was not found in the contract");
            return Regex.Matches("\"\"document\"\"," + m.Groups[1].Value, @"""""([a-z_]+)""""")
                        .Cast<Match>().Select(x => x.Groups[1].Value).ToList();
        }

        [Fact]
        public void Every_declared_section_is_actually_built_by_a_call()
        {
            // A name offered, accepted, and answering nothing.
            string src = WithoutComments(Scan());
            var missing = DeclaredSections()
                .Where(s => !Regex.IsMatch(src, @"Section\(result, failed, skipped, sections, """ + s + @"""[,\)]"))
                .ToList();
            Assert.True(missing.Count == 0,
                "declared in AllSections but never built: " + string.Join(", ", missing));
        }

        [Fact]
        public void Every_built_section_is_declared_so_it_can_be_asked_for()
        {
            // Built but undeclared is worse than missing: the section reports
            // "not_requested" and the reply reads as though somebody chose that.
            List<string> declared = DeclaredSections();
            var built = Regex.Matches(Scan(), @"Section\(result, failed, skipped, sections, ""([a-z_]+)""")
                             .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(built);

            var undeclared = built.Where(b => !declared.Contains(b)).ToList();
            Assert.True(undeclared.Count == 0,
                "built but not in AllSections, so nobody can request it: " + string.Join(", ", undeclared));
        }

        [Fact]
        public void Every_declared_section_is_in_the_contract_the_caller_is_validated_against()
        {
            // THE WIRING FAILURE THAT LOOKS LIKE THE CALLER'S FAULT: the tool
            // advertises a section its own schema then refuses.
            List<string> declared = DeclaredSections();
            List<string> inContract = ContractSections();

            var missing = declared.Where(d => !inContract.Contains(d)).ToList();
            Assert.True(missing.Count == 0,
                "in AllSections but rejected by the schema: " + string.Join(", ", missing));
        }

        [Fact]
        public void The_contract_offers_no_section_the_scan_cannot_build()
        {
            // The mirror image: a schema that accepts a name nothing implements
            // produces an empty answer with no error anywhere.
            List<string> declared = DeclaredSections();
            var extra = ContractSections().Where(c => !declared.Contains(c)).ToList();
            Assert.True(extra.Count == 0,
                "accepted by the schema but not implemented: " + string.Join(", ", extra));
        }

        [Fact]
        public void The_sections_added_for_the_doctor_are_all_four_places_at_once()
        {
            // Named explicitly, so deleting one from AllSections fails HERE with the
            // name in the message rather than as a count that drifted.
            List<string> declared = DeclaredSections();
            foreach (string s in new[] { "coordinates", "datums", "level_association", "weight" })
                Assert.True(declared.Contains(s), "section missing from AllSections: " + s);
        }

        [Fact]
        public void Weight_is_built_last_because_it_reads_what_the_others_emitted()
        {
            // It ranks the sections' own output. Built earlier, it would rank a
            // reply that does not exist yet and report every contributor absent.
            string src = WithoutComments(Scan());
            int weight = src.IndexOf(@"sections, ""weight""", StringComparison.Ordinal);
            Assert.True(weight > 0, "the weight section is not built at all");

            foreach (string s in DeclaredSections().Where(x => x != "weight"))
            {
                int at = src.IndexOf(@"sections, """ + s + @"""", StringComparison.Ordinal);
                Assert.True(at > 0 && at < weight,
                    "'" + s + "' is built after 'weight', which reads the sections above it");
            }
        }

        // ------------------------------------------------------------------
        // REQUEST KEYS: the same cross-check, one level up.
        //
        // Written after getting it wrong. `warning_profile` was added to the
        // schema by hand and landed INSIDE another property's object. The JSON
        // still parsed, every suite stayed green, and the only visible effect
        // would have been the server rejecting the very option the tool had just
        // started accepting - because additionalProperties is false and the key
        // was not, in fact, a property.
        //
        // A schema that parses is not a schema that says what you meant.
        // ------------------------------------------------------------------

        /// <summary>The verbatim @"..." schema string that follows a tool's name.</summary>
        private static JObject SchemaOf(string toolName)
        {
            string c = Contract();
            // ANCHORED ON THE DECLARATION, not on the name appearing anywhere. A bare
            // quoted name also matches a tool MENTIONED by another tool's schema - an
            // enum of admissible tools, for instance - and every such mention that
            // precedes the real definition made this read a different tool's schema
            // and pass. Found once, by adding exactly that enum.
            int at = c.IndexOf("Name = \"" + toolName + "\"", StringComparison.Ordinal);
            Assert.True(at >= 0, "tool not declared in the contract: " + toolName);

            int cursor = at;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int open = c.IndexOf("@\"", cursor, StringComparison.Ordinal);
                if (open < 0) break;
                open += 2;

                var sb = new System.Text.StringBuilder();
                int j = open;
                while (j < c.Length)
                {
                    if (c[j] == '"')
                    {
                        if (j + 1 < c.Length && c[j + 1] == '"') { sb.Append('"'); j += 2; continue; }
                        break;
                    }
                    sb.Append(c[j]); j++;
                }
                string text = sb.ToString();
                cursor = j + 1;

                if (text.Contains("\"properties\"") && text.Contains("\"additionalProperties\""))
                    return JObject.Parse(text);
            }
            Assert.Fail("no schema with properties/additionalProperties found after " + toolName);
            return null;
        }

        /// <summary>A named string[] in ScanRequestRules, read from the source.</summary>
        private static List<string> KnownKeys(string arrayName)
        {
            string src = File.ReadAllText(Path.Combine(Root().FullName,
                "src", "Horizun.Revit", "Core", "ScanRequestRules.cs"));
            int i = src.IndexOf(arrayName + " =", StringComparison.Ordinal);
            Assert.True(i >= 0, "array not found: " + arrayName);
            int open = src.IndexOf('{', i);
            int close = src.IndexOf("};", open, StringComparison.Ordinal);
            List<string> keys = Regex.Matches(src.Substring(open, close - open), "\"([a-z_]+)\"")
                        .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            // An empty list would make every comparison below vacuously true, which
            // is the way a cross-check quietly stops checking.
            Assert.True(keys.Count >= 4, arrayName + " parsed as " + keys.Count + " keys, which cannot be right");
            return keys;
        }

        [Theory]
        [InlineData("horizun_model_scan", "KnownKeys")]
        [InlineData("horizun_audit_model", "AuditKnownKeys")]
        public void Every_key_the_command_accepts_is_a_property_the_schema_accepts(string tool, string array)
        {
            JObject schema = SchemaOf(tool);
            var props = ((JObject)schema["properties"]).Properties().Select(p => p.Name).ToList();
            Assert.NotEmpty(props);

            // additionalProperties:false is what makes this consequential: a key
            // that is not a property is REFUSED, not merely undocumented.
            Assert.False(schema.Value<bool>("additionalProperties"),
                tool + " does not close its schema, so this check would not matter");

            var missing = KnownKeys(array).Where(k => !props.Contains(k)).ToList();
            Assert.True(missing.Count == 0,
                tool + " accepts these but its schema has no such property, so the server refuses them: " +
                string.Join(", ", missing));
        }

        [Theory]
        [InlineData("horizun_model_scan", "KnownKeys")]
        [InlineData("horizun_audit_model", "AuditKnownKeys")]
        public void The_schema_offers_no_option_the_command_would_reject(string tool, string array)
        {
            // The mirror image: documented, accepted by the schema, then refused by
            // the command's own unknown-key check with a list that does not mention it.
            JObject schema = SchemaOf(tool);
            List<string> known = KnownKeys(array);
            var extra = ((JObject)schema["properties"]).Properties().Select(p => p.Name)
                        .Where(p => !known.Contains(p)).ToList();
            Assert.True(extra.Count == 0,
                tool + " documents these but the command refuses them as unknown: " + string.Join(", ", extra));
        }

        [Fact]
        public void The_unobservable_link_position_is_never_published_as_a_measurement_that_ran()
        {
            // SharedPositionMatchesHost is assigned in exactly one place, as null,
            // because the Revit API exposes no read path for it in any supported
            // year. So the count is identically 0 - and publishing it as a
            // measurement that RAN made `max_links_not_sharing_position: 0` answer
            // "0 against a limit of 0, with complete coverage": a pass, forever, on
            // a fact nobody looked at, in the gate a team runs before a delivery.
            string audit = File.ReadAllText(Path.Combine(Root().FullName,
                "src", "Horizun.Revit", "Commands", "AuditModelCommand.cs"));

            int at = audit.IndexOf("CoordinateCheckParts.LinksNotSharingPosition", StringComparison.Ordinal);
            Assert.True(at >= 0, "the links-not-sharing-position part is gone");
            string near = audit.Substring(at, Math.Min(220, audit.Length - at));

            Assert.Contains("NotMeasured()", near);
            Assert.DoesNotContain("Part(notSharing", near);

            // And NotMeasured must really mean it: Ran = false is what stops the
            // gate passing, and Count = null is what stops it reading as zero.
            Assert.Contains("Ran = false", audit);
        }

        [Fact]
        public void The_warning_profile_is_a_top_level_property_of_both_tools()
        {
            // Named explicitly because this is the one that was wrong: it parsed as
            // valid JSON while sitting inside another property's object.
            foreach (string tool in new[] { "horizun_model_scan", "horizun_audit_model" })
                Assert.True(((JObject)SchemaOf(tool)["properties"])["warning_profile"] != null,
                    "warning_profile is not a top-level property of " + tool);
        }

        // ------------------------------------------------------------------
        // NAMING CLASSES: the profile can mention fourteen, so fourteen must be
        // collected. A class the collector forgets is reported not_collected at
        // runtime - which is honest, but it is still a hole, and a hole nobody
        // fails a build over is a hole that ships.
        // ------------------------------------------------------------------

        [Fact]
        public void Every_naming_class_is_collected_or_explicitly_declared_absent()
        {
            string raw = Scan();
            int i = raw.IndexOf("NamingPopulations(", StringComparison.Ordinal);
            Assert.True(i >= 0, "NamingPopulations not found");
            int end = raw.IndexOf("private static JObject NamingSection", i, StringComparison.Ordinal);
            Assert.True(end > i, "could not bound NamingPopulations");
            string body = WithoutComments(raw.Substring(i, end - i));

            var collected = Regex.Matches(body, @"collect\(""([a-z_]+)""")
                                 .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var declaredAbsent = Regex.Matches(body, @"Class = ""([a-z_]+)""")
                                      .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(collected);

            var handled = new HashSet<string>(collected.Concat(declaredAbsent), StringComparer.Ordinal);
            var missing = NamingClasses.All.Where(c => !handled.Contains(c)).ToList();
            Assert.True(missing.Count == 0,
                "the naming profile accepts these classes but nothing collects them: " +
                string.Join(", ", missing));
        }

        [Fact]
        public void Nothing_is_collected_under_a_class_the_profile_does_not_know()
        {
            // A population filed under a name no rule can mention is collected,
            // judged against nothing, and reported as if it were assessed.
            string raw = Scan();
            int i = raw.IndexOf("NamingPopulations(", StringComparison.Ordinal);
            int end = raw.IndexOf("private static JObject NamingSection", i, StringComparison.Ordinal);
            string body = WithoutComments(raw.Substring(i, end - i));

            var known = new HashSet<string>(NamingClasses.All, StringComparer.Ordinal);
            var strays = Regex.Matches(body, @"collect\(""([a-z_]+)""")
                              .Cast<Match>().Select(m => m.Groups[1].Value)
                              .Where(c => !known.Contains(c)).ToList();
            Assert.True(strays.Count == 0,
                "collected under a class NamingProfileRules cannot judge: " + string.Join(", ", strays));
        }

        // ------------------------------------------------------------------
        // THE OWNERSHIP CENSUS MUST NOT TAKE ANYTHING.
        //
        // The reason this section exists is that the only previous way to count
        // borrowed elements was to relinquish them - a question that changed the
        // answer, and changed it for everybody on the team. A diagnostic that
        // acquires or releases ownership is not a diagnostic, and the mistake
        // would be one autocompleted call away.
        // ------------------------------------------------------------------

        [Fact]
        public void The_worksharing_section_neither_takes_nor_releases_ownership()
        {
            string body = Body("WorksharingSection", "// ============================== coordinates");

            foreach (string forbidden in new[]
                     {
                         "RelinquishOwnership", "RelinquishAll", "CheckoutElements", "CheckoutWorksets",
                         "SynchronizeWithCentral", "new Transaction", "TransactWithCentralOptions"
                     })
                Assert.False(body.Contains(forbidden),
                    "the ownership census calls '" + forbidden + "', which changes the model it is measuring");

            // And it must actually be reading the status, not inferring it from
            // something cheaper that does not mean the same thing.
            Assert.Contains("WorksharingUtils.GetCheckoutStatus", body);
        }

        [Fact]
        public void A_document_that_is_not_workshared_short_circuits_before_any_element_is_read()
        {
            // Walking every element to classify it as "not owned" would produce
            // four zeros - a census that ran - about a file that has no ownership
            // at all, which is a different claim.
            string body = Body("WorksharingSection", "// ============================== coordinates");

            int guard = body.IndexOf("if (workshared != true)", StringComparison.Ordinal);
            int walk = body.IndexOf("FilteredElementCollector", StringComparison.Ordinal);
            Assert.True(guard > 0, "no not-workshared guard in the worksharing section");
            Assert.True(walk > guard,
                "the element walk starts before the not-workshared guard, so a file with no ownership " +
                "would be reported as a census that found none");
            Assert.Contains("NotApplicable", body);
        }

        // ------------------------------------------------------------------
        // THE FAMILY CENSUS MUST NOT OPEN A FAMILY.
        //
        // Document.EditFamily opens the .rfa as a document. That changes which
        // document is active, which is the one thing every command in this bridge
        // is required to be certain about - and a scan that changes what it is
        // measuring has stopped being a scan. It is also the obvious way somebody
        // would try to answer "how big is this family", which the census refuses
        // to answer at all.
        // ------------------------------------------------------------------

        [Fact]
        public void The_family_census_never_opens_a_family_document()
        {
            string body = Body("FamiliesSection", "// ============================== worksharing");

            foreach (string forbidden in new[]
                     { "EditFamily", "OpenDocumentFile", "LoadFamily", "OpenAndActivateDocument" })
                Assert.False(body.Contains(forbidden),
                    "the family census calls '" + forbidden + "', which opens a document while scanning one");
        }

        [Fact]
        public void A_system_family_is_collected_by_a_route_that_is_not_OfClass_Family()
        {
            // A wall type has no Family element. Collected only through
            // OfClass(Family), the census reports fewer families than the model
            // has and says nothing about it.
            string body = Body("FamiliesSection", "// ============================== worksharing");

            Assert.Contains("WhereElementIsElementType", body);
            Assert.Contains("is FamilySymbol", body);
            Assert.Contains("FamilyKind.System", body);
        }

        [Fact]
        public void The_family_census_reads_the_shared_flag_as_three_valued()
        {
            // FAMILY_SHARED is frequently ABSENT, and absent is not "this family is
            // not shared". Written as `param != null && value`, an absent parameter
            // becomes a confident false - a claim the model never made. This is a
            // source guard because the defect lives in the Revit-side read, where a
            // desk test cannot reach it.
            string body = Body("FamiliesSection", "// ============================== worksharing");

            Assert.Contains("BuiltInParameter.FAMILY_SHARED", body);
            Assert.Contains("shared == null ? (bool?)null", body);
            Assert.False(body.Contains("shared != null &&"),
                "the shared flag is read as a plain bool, so an absent parameter becomes a confident false");
        }

        // ------------------------------------------------------------------
        // VIEWS: two invariants that live in the command, not in the rules.
        // ------------------------------------------------------------------

        private static string ViewsBody()
        {
            return Body("ViewsSection", "// ================================ families");
        }

        [Fact]
        public void A_view_template_is_never_judged_against_rules_written_for_drawings()
        {
            // "Views without a template" that counts the templates themselves is the
            // usual way this area produces a large, confident, meaningless number.
            string body = ViewsBody();
            Assert.Contains("f.IsTemplate ? new List<ViewPropertyVerdict>()", body);
            Assert.Contains("ViewFactsRules.Judge(f, profile)", body);
        }

        [Fact]
        public void Revits_own_internal_views_are_excluded_before_anything_is_judged()
        {
            // ProjectBrowser and friends are Revit's furniture, not somebody's
            // drawing. Judged, they fail every rule and drown the real findings.
            string body = ViewsBody();
            Assert.Contains("ViewApplicability.IsInternal", body);
            int guard = body.IndexOf("ViewApplicability.IsInternal", StringComparison.Ordinal);
            int judge = body.IndexOf("ViewFactsRules.Judge", StringComparison.Ordinal);
            Assert.True(guard > 0 && guard < judge,
                "internal views reach the judgement before being excluded");
        }

        [Fact]
        public void A_sheet_is_not_counted_among_the_views()
        {
            // A ViewSheet IS a View. Left in, every sheet is judged against rules
            // for drawings and counted twice in the documentation numbers.
            Assert.Contains("if (v is ViewSheet) continue;", ViewsBody());
        }

        [Fact]
        public void Every_view_property_read_is_guarded_and_names_the_property_it_failed()
        {
            // A bare try/catch would turn an unreadable property into a null, and a
            // null into a rule that failed. The named set is what keeps
            // not_readable distinct from failed.
            string body = ViewsBody();
            Assert.Contains("f.Unreadable.Add(property)", body);
            Assert.True(body.Split(new[] { "Read(f, ViewProperties." }, StringSplitOptions.None).Length - 1 >= 8,
                "most view properties are not read through the guarded helper");
        }

        // ------------------------------------------------------------------
        // SHEETS AND ANNOTATIONS: what the command must keep apart.
        // ------------------------------------------------------------------

        /// <summary>
        /// One method's body, with comments blanked.
        ///
        /// The BOUNDS are found in the raw source and only the slice is stripped:
        /// the boundaries between sections are banner COMMENTS, so stripping first
        /// erases the very markers used to find the end.
        /// </summary>
        /// <summary>Bounds a helper that does not return JObject.</summary>
        private static string Body2(string method, string nextBanner)
        {
            string raw = Scan();
            // The DECLARATION, not the first call site. Searching for `method(`
            // alone finds where the helper is invoked, and the bound then lands
            // before its body - which is how this guard first came back failing
            // against code that was correct.
            Match decl = Regex.Match(raw, @"private static [\w<>,\[\]\s]+ " + method + @"\(");
            Assert.True(decl.Success, "declaration not found: " + method);
            int i = decl.Index;
            Assert.True(i >= 0, method + " not found");
            int end = raw.IndexOf(nextBanner, i, StringComparison.Ordinal);
            Assert.True(end > i, "could not bound " + method);
            return WithoutComments(raw.Substring(i, end - i));
        }

        private static string Body(string method, string nextBanner)
        {
            string raw = Scan();
            int i = raw.IndexOf("private static JObject " + method, StringComparison.Ordinal);
            Assert.True(i >= 0, method + " not found");
            int end = raw.IndexOf(nextBanner, i, StringComparison.Ordinal);
            Assert.True(end > i, "could not bound " + method);
            return WithoutComments(raw.Substring(i, end - i));
        }

        [Fact]
        public void A_schedule_on_a_sheet_is_collected_apart_from_the_viewports()
        {
            // Revit places a schedule as a ScheduleSheetInstance. Counted as sheet
            // contents through GetAllViewports it does not appear at all, and a
            // sheet of schedules is reported empty.
            string body = Body("SheetsSection", "private static JObject ViewportJson");

            // The COLLECTOR has to exist, not merely the words. A guard that only
            // asserts the field name is satisfied by code that fills it with zero -
            // which is how a mutation doing exactly that came back vacuous.
            Assert.Contains("OfClass(typeof(ScheduleSheetInstance))", body);
            Assert.Contains("schedules.TryGetValue(id, out n) ? n : 0", body);
            Assert.Contains("GetAllViewports", body);
            Assert.False(body.Contains("f.ScheduleInstanceCount = 0;"),
                "the schedule count is hard-wired to zero, so a sheet of schedules reads as empty");
        }

        [Fact]
        public void The_annotation_census_counts_only_view_specific_elements()
        {
            // A door tag lives in one view; the door does not. Counting both gives
            // a documentation number that grows when somebody models a wall.
            string body = Body("AnnotationsSection", "private static string SafeViewType");

            // There are TWO collection loops here - one by category, one by class -
            // and each needs its own check. Asserting the text appears at all is
            // satisfied when one of the two loses it, which is how a mutation
            // removing exactly one came back vacuous.
            int checks = body.Split(new[] { "if (!e.ViewSpecific)" }, StringSplitOptions.None).Length - 1;
            Assert.True(checks >= 2,
                "only " + checks + " of the annotation collection loops filter to view-specific elements");
            Assert.Contains("notViewSpecific++", body);
        }

        [Fact]
        public void Tags_are_collected_by_class_because_they_span_many_categories()
        {
            // There is no OST_Tags. A fixed list of tag categories silently misses
            // every tag category not on it.
            string body = Body("AnnotationsSection", "private static string SafeViewType");
            Assert.Contains("typeof(IndependentTag)", body);
            Assert.Contains("typeof(FilledRegion)", body);
        }

        // ------------------------------------------------------------------
        // PARAMETERS: two things the command must get right that no desk test
        // of the rules can reach.
        // ------------------------------------------------------------------

        [Fact]
        public void The_specification_is_read_from_GetDataType_and_never_from_ParameterGroup()
        {
            // Definition.ParameterGroup exists in Revit 2023 and is GONE by 2027.
            // Reading it would compile on the oldest supported year and break on
            // the newest - the exact shape of failure the five-year matrix exists
            // to catch, and cheaper to forbid here.
            string body = Body("ParametersSection", "// ============================== annotations");
            Assert.Contains("GetDataType()", body);
            Assert.False(body.Contains("ParameterGroup"),
                "the specification is read from ParameterGroup, which does not exist in Revit 2027");
        }

        [Fact]
        public void A_type_parameter_is_observed_once_with_its_instances_attached()
        {
            // Observing per instance turns one wrong type into one finding per
            // instance and buries the rest of the report.
            string body = Body("ParametersSection", "// ============================== annotations");
            Assert.Contains("WhereElementIsElementType()", body);
            Assert.Contains("AffectedInstanceIds.AddRange", body);
        }

        [Fact]
        public void Redundancy_is_never_inferred_from_a_zero_area()
        {
            // Revit exposes no IsRedundant. A redundant room reports zero area and
            // no boundary exactly as an unenclosed one does, so any code deriving
            // redundancy from the area is guessing - and guessing sends somebody to
            // hunt a boundary leak that does not exist.
            string body = Body("SpatialSection", "// ============================== parameters");
            Assert.Contains("redundantGuids", body);
            Assert.Contains("GetFailureDefinitionId", body);
            Assert.False(body.Contains("IsRedundant = f.AreaSqM"),
                "redundancy is being derived from the area");
        }

        [Fact]
        public void Enclosure_comes_from_the_boundary_and_placement_from_the_location()
        {
            // Three states, three different reads. Deriving any of them from the
            // area is the single condition this whole area exists to avoid.
            string body = Body("SpatialSection", "// ============================== parameters");
            Assert.Contains("GetBoundarySegments", body);
            Assert.Contains("se.Location != null", body);
        }

        // ------------------------------------------------------------------
        // GROUPS AND DESIGN OPTIONS: two invariants that live in the command.
        // ------------------------------------------------------------------

        [Fact]
        public void A_group_type_that_is_never_placed_reports_an_unknown_member_count()
        {
            // A GroupType does not enumerate its own members: the list comes from a
            // PLACED instance. A type nothing places therefore has an unknown member
            // count, and writing 0 there calls a full group empty - the exact
            // confusion the whole area separates.
            string body = Body("GroupsSection", "// ============================= design options");
            Assert.Contains("f.MemberCount = null;", body);
            Assert.False(body.Contains("f.MemberCount = 0;"),
                "an unplaced group type is reported as holding zero members");
        }

        [Fact]
        public void A_document_with_no_design_options_short_circuits_before_the_element_walk()
        {
            // Walking every element to attribute it to no option produces counts -
            // a check that RAN - about a document that has no design options at all.
            string body = Body("DesignOptionsCensus", "private static JObject WorksharingSection");
            // The guard must be REACHABLE, not merely present: `if (false) return
            // NoDesignOptions();` keeps the call in the source and before the walk
            // while never running, which is how a mutation of it came back vacuous.
            Assert.Contains("if (sets.Count == 0) return GroupOptionRules.NoDesignOptions();", body);

            int guard = body.IndexOf("NoDesignOptions()", StringComparison.Ordinal);
            // The MODEL-WIDE walk specifically: collecting the option SETS also
            // uses WhereElementIsNotElementType and legitimately runs first, since
            // it is what the guard tests.
            int walk = body.IndexOf(
                "foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())",
                StringComparison.Ordinal);
            Assert.True(guard > 0, "no not-applicable guard in the design option census");
            Assert.True(walk > guard,
                "the element walk starts before the no-options guard, so a document without design options " +
                "is reported as a census that found none");
        }

        [Fact]
        public void Phase_applicability_is_asked_of_the_element_not_read_from_a_list()
        {
            // A compiled-in list of phase-carrying categories drifts with every
            // Revit release and becomes a wrong answer nobody notices. The element
            // is asked for the parameter instead, and BOTH being absent is what
            // makes the category not_applicable.
            string body = Body("PhasesSection", "// ================================ groups");
            Assert.Contains("if (created == null && demolished == null)", body);
            Assert.Contains("f.SupportsPhases = false;", body);
        }

        [Fact]
        public void Phase_order_comes_from_the_document_sequence_and_never_from_the_name()
        {
            // "Phase 10" sorts before "Phase 2" as text, and every before/after
            // question in the section would then be wrong.
            string body = Body("PhasesSection", "// ================================ groups");
            Assert.Contains("foreach (Phase ph in doc.Phases)", body);
            Assert.Contains("sequenceOf[id] = i;", body);
        }

        // ------------------------------------------------------------------
        // MEP AND STRUCTURE: what the sections must not claim, and one Revit
        // fact each that a category filter alone gets wrong.
        // ------------------------------------------------------------------

        [Fact]
        public void Neither_mep_nor_structure_claims_anything_it_cannot_observe()
        {
            // The words that mark the slide from "modelled" to "correct". None of
            // them is supportable from a Revit document, and a report that uses
            // them will be acted on.
            string mep = Body("MepSection", "// ================================ structure");
            string structure = Body("StructureSection", "private static void CountStructuralHosts");

            foreach (string forbidden in new[]
                     { "IsBalanced", "PressureDrop", "FlowRate", "Utilisation", "Utilization", "CodeCompliant" })
            {
                Assert.False(mep.Contains(forbidden), "the MEP section reads '" + forbidden + "'");
                Assert.False(structure.Contains(forbidden), "the structure section reads '" + forbidden + "'");
            }
        }

        [Fact]
        public void A_structural_wall_is_a_wall_with_the_flag_and_not_the_whole_category()
        {
            // Counting OST_Walls entire would report every partition in the model
            // as structure, which is a large and confidently wrong number.
            string body = Body("StructureSection", "private static void CountStructuralHosts");
            Assert.Contains("CountStructuralHosts(doc, BuiltInCategory.OST_Walls", body);

            string helper = Body2("CountStructuralHosts", "// ================================ phases");
            Assert.Contains("WALL_STRUCTURAL_SIGNIFICANT", helper);
            Assert.Contains("FLOOR_PARAM_IS_STRUCTURAL", helper);
            // And the flag must actually be TESTED. Reading the parameter and then
            // counting everything anyway keeps both names in the source while
            // reporting every partition as structure.
            Assert.Contains("p.AsInteger() == 0) continue;", helper);
        }

        [Fact]
        public void An_mep_element_is_found_by_its_connectors_and_not_by_a_category_list()
        {
            // A list of MEP categories drifts with every release and with every
            // discipline somebody adds. Having a ConnectorManager is what makes an
            // element MEP, and an element without one is skipped rather than
            // reported as a duct with no system.
            string body = Body("MepSection", "// ================================ structure");
            Assert.Contains("ConnectorManager", body);
            Assert.Contains("if (cm == null) continue;", body);
        }

        [Fact]
        public void Nested_links_come_from_the_type_graph_and_nothing_opens_a_file()
        {
            // RevitLinkType.GetChildIds answers from the type graph in all five
            // supported years - verified by reflection, see
            // docs/evidence/coordinate-and-datum-api-evidence.md.
            //
            // This guard used to demand GetLinkDocument(), which is WEAKER: it
            // returns null for an unloaded link, so nesting the model knew came
            // back as "unreadable". Reading the type graph answers loaded or not,
            // and still opens nothing.
            string body = Body("FederationSection", "// ============================ external content");
            Assert.Contains("lt.GetChildIds()", body);
            foreach (string forbidden in new[]
                     { "OpenDocumentFile", "OpenAndActivateDocument", "EditFamily", "GetLinkDocument" })
                Assert.False(body.Contains(forbidden),
                    "the federation section calls " + forbidden + ", which either opens a document while " +
                    "scanning one or makes nesting depend on the link being loaded");
        }

        [Fact]
        public void Nesting_does_not_depend_on_the_link_being_loaded()
        {
            // The property that replaced "an unloaded link reports unreadable": it
            // no longer has to. An unloaded link still has children in the type
            // graph, and reporting unreadable there was a false negative.
            string body = Body("FederationSection", "// ============================ external content");
            Assert.Contains("lt.IsNestedLink", body);
            Assert.False(body.Contains("Document linked"),
                "nesting is still routed through a linked Document, which is null when the link is unloaded");
        }

        [Fact]
        public void External_content_separates_a_missing_path_from_no_path_at_all()
        {
            // A texture whose file moved and a material that never had one are
            // different problems; deriving both from a null merges them.
            string body = Body("ExternalContentSection", "private static bool FileExists");
            Assert.Contains("string.IsNullOrWhiteSpace(f.Path) ? (bool?)null : FileExists(f.Path)", body);
        }

        [Fact]
        public void Decals_are_never_reported_as_a_count()
        {
            // There is no way to count them in any supported year, and a zero would
            // be a count.
            string body = Body("ExternalContentSection", "private static bool FileExists");
            Assert.Contains("not_observable", body);
            Assert.False(body.Contains("OST_Decals"),
                "decals are being counted through a category that does not exist in the supported years");
        }

        [Fact]
        public void Scope_box_extents_are_read_off_the_scope_box_element_itself()
        {
            // The mandate's explicit warning: a bounding box taken from the
            // elements a scope box crops is a guess shaped like a measurement, and
            // it is wrong on the box that crops more than it holds. The read must
            // be `e.get_BoundingBox` on the scope box being iterated - not a
            // collector reaching for some other element.
            //
            // This is a SOURCE guard because the substitution lives in the Revit
            // read, where a desk test of the rules cannot reach it.
            string body = Body2("ReadScopeBoxes", "/// <summary>");
            Assert.Contains("e.get_BoundingBox(null)", body);
            Assert.False(body.Contains("FirstElement()"),
                "the scope box extents are taken from some other element");
        }

        [Fact]
        public void The_documentary_read_separates_absent_from_blank_at_the_source()
        {
            // The Core rules keep absent and blank apart, but only if the READ
            // supplies them apart. A field whose parameter does not exist must set
            // Present=false; setting it true would hand the rules a blank and lose
            // the distinction before they ever see it.
            string body = Body2("ReadDocumentaryField", "/// <summary>");
            Assert.Contains("if (p == null) { f.Present = false; return f; }", body);
            // and a throw is its own third state, not either of those two
            Assert.Contains("catch { f.Readable = false; }", body);
        }

        [Fact]
        public void A_readiness_role_is_measured_only_on_the_categories_it_declares()
        {
            // Silently widening a role to the whole model is how a rule about doors
            // becomes a finding about ducts - and how a per-category measurement
            // turns back into the model-wide average it exists to replace.
            string body = Body("DeliveryReadinessSection", "/// <summary>");
            Assert.Contains("foreach (string category in role.Rule.Categories)", body);
        }

        [Fact]
        public void The_readiness_walk_happens_once_per_role_and_category()
        {
            // The ids and the counts must come from ONE walk: a second pass is a
            // second full traversal of the model, and two walks can disagree with
            // each other about the same question.
            string body = Body("DeliveryReadinessSection", "/// <summary>");
            int walks = body.Split(new[] { "MeasureRoleOnCategory(" }, StringSplitOptions.None).Length - 1;
            Assert.True(walks == 1,
                "the readiness section measures " + walks + " times per role and category");
        }

        [Fact]
        public void No_classification_catalogue_is_compiled_into_the_command()
        {
            // The taxonomy arrives as an argument. A code list in the source would
            // enforce one organisation's standard on everybody's model.
            string body = Body("DeliveryReadinessSection", "/// <summary>");
            Assert.Contains("classificationCatalogue", Scan());
            foreach (string forbidden in new[] { "OmniClass", "UniFormat", "MasterFormat" })
                Assert.False(body.Contains(forbidden),
                    "a named taxonomy (" + forbidden + ") appears in the readiness section");
        }
    }
}
