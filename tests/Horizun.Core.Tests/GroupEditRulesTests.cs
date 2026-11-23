// Group member edits, set_curve verification, the Python move watch, level-span
// exceptions, cloud-cache saves and focus_window: the Revit-free halves and the
// source seams that wire them in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed class GroupEditRulesTests
{
    private static double[] P(double x, double y, double z) => new[] { x, y, z };

    private static string Source(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root.FullName }.Concat(parts).ToArray()));
    }

    // ---------------------------------------------------------------- point 1
    [Theory]
    [InlineData("move", true)] [InlineData("MOVE_preserve_pin", true)] [InlineData("rotate", true)]
    [InlineData("mirror", true)] [InlineData("set_curve", true)] [InlineData("array_linear", true)]
    [InlineData("pin", false)] [InlineData("change_type", false)] [InlineData("copy", false)] [InlineData(null, false)]
    public void Only_operations_that_place_an_element_are_guarded(string op, bool guarded)
        => Assert.Equal(guarded, GroupMemberGuard.IsGeometric(op));

    [Fact]
    public void The_refusal_names_the_code_each_member_and_both_ways_through()
    {
        string m = GroupMemberGuard.Message(new List<GroupMemberTarget>
        {
            new GroupMemberTarget { ElementId = 101, GroupId = 900, GroupTypeName = "Tipo A", TypeInstances = 29, Operation = "move" },
            new GroupMemberTarget { ElementId = 102, GroupId = 900, GroupTypeName = "Tipo A", TypeInstances = 29, Operation = "set_curve" }
        });
        Assert.StartsWith("miembro_de_grupo: ", m);
        Assert.Contains("101 (move) is a member of group 900 'Tipo A', whose type has 29 instance(s)", m);
        Assert.Contains("102 (set_curve)", m);
        Assert.Contains("more than one instance", m);
        Assert.Contains("edit_type_members", m);
        Assert.Contains("operation=ungroup", m);
        Assert.Contains("Nothing was written", m);
        Assert.Null(GroupMemberGuard.Message(new List<GroupMemberTarget>()));
    }

    [Fact]
    public void A_single_instance_group_is_not_described_as_failing_at_commit()
    {
        string m = GroupMemberGuard.Message(new List<GroupMemberTarget> { new GroupMemberTarget { ElementId = 5, GroupId = 6, TypeInstances = 1, Operation = "rotate" } });
        Assert.DoesNotContain("more than one instance", m);
    }

    [Fact]
    public void Miembro_de_grupo_is_a_stable_catalogued_code()
    {
        Assert.Equal(GroupMemberGuard.Code, ErrorCodes.MiembroDeGrupo);
        Assert.True(ErrorCodes.IsStable("miembro_de_grupo"));
        var d = ErrorCodes.Ensure("anything", ErrorCodes.Detail(ErrorCodes.MiembroDeGrupo));
        Assert.Equal("miembro_de_grupo", (string)d["error_code"]);
    }

    [Fact]
    public void Transform_refuses_group_members_before_it_plans_anything()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "TransformElementsCommand.cs");
        int refusal = s.IndexOf("CommandResult memberRefusal = RefuseGroupMembers(doc, input);", StringComparison.Ordinal);
        int planning = s.IndexOf("Plan plan = PlanOperation(doc, i, input[i] as JObject", StringComparison.Ordinal);
        Assert.True(refusal > 0 && planning > refusal);
        Assert.Contains("GroupMemberGuard.IsGeometric(op)", s);
        Assert.Contains("FallbackSignal.NotAllowed(GroupMemberGuard.Code, false)", s);
        // set_curve re-reads and compares against the curve it had before.
        Assert.Contains("CurveChangeRules.Judge(D3(was[0]), D3(was[was.Count - 1])", s);
        Assert.Contains("curve_mismatches", s);
    }

    [Fact]
    public void A_collinear_set_curve_that_did_not_happen_is_unchanged_not_verified()
    {
        // Extending a pipe along its own line: the old ends are "on the requested line" too.
        double t = 0.5 / 304.8;
        string v = CurveChangeRules.Judge(P(0, 0, 0), P(10, 0, 0), P(0, 0, 0), P(12, 0, 0), P(0, 0, 0), P(10, 0, 0), t);
        Assert.Equal(CurveChangeRules.Unchanged, v);
        Assert.Equal(CurveChangeRules.Verified, CurveChangeRules.Judge(P(0, 0, 0), P(10, 0, 0), P(0, 0, 0), P(12, 0, 0), P(0, 0, 0), P(12, 0, 0), t));
        // Reversed ends are the same curve.
        Assert.Equal(CurveChangeRules.Unchanged, CurveChangeRules.Judge(P(0, 0, 0), P(10, 0, 0), P(0, 0, 0), P(12, 0, 0), P(10, 0, 0), P(0, 0, 0), t));
        // A join that trims the end back is still on the line, and the curve did change.
        Assert.Equal(CurveChangeRules.Verified, CurveChangeRules.Judge(P(0, 0, 0), P(10, 0, 0), P(0, 5, 0), P(10, 5, 0), P(0.3, 5, 0), P(10, 5, 0), t));
        Assert.Equal(CurveChangeRules.OffLine, CurveChangeRules.Judge(P(0, 0, 0), P(10, 0, 0), P(0, 5, 0), P(10, 5, 0), P(0, 4, 0), P(10, 4, 0), t));
        // Asking for exactly the curve it already has is judged on the line alone.
        Assert.Equal(CurveChangeRules.Verified, CurveChangeRules.Judge(P(0, 0, 0), P(10, 0, 0), P(0, 0, 0), P(10, 0, 0), P(0, 0, 0), P(10, 0, 0), t));
    }

    [Fact]
    public void Python_move_calls_are_recognised_and_comparisons_are_not()
    {
        Assert.True(PythonGroupMoveScan.CallsMove("ElementTransformUtils.MoveElement(doc, e.Id, v)"));
        Assert.True(PythonGroupMoveScan.CallsMove("ElementTransformUtils.MoveElements(doc, ids, v)"));
        Assert.True(PythonGroupMoveScan.CallsMove("e.Location.Move(v)"));
        Assert.True(PythonGroupMoveScan.CallsMove("e.Location.Point = XYZ(0,0,1)"));
        Assert.True(PythonGroupMoveScan.CallsMove("lc.Curve = Line.CreateBound(a, b)"));
        Assert.True(PythonGroupMoveScan.CallsMove("ElementTransformUtils.RotateElement(doc, i, ax, 1)"));
        Assert.False(PythonGroupMoveScan.CallsMove("if e.Location.Point == p: pass"));
        Assert.False(PythonGroupMoveScan.CallsMove("walls = FilteredElementCollector(doc).OfClass(Wall)"));
        Assert.Equal(new[] { 765199L, 42L }, PythonGroupMoveScan.LiteralIds("a=ElementId(765199)\nb = ElementId( 42 )\nc=ElementId(x)").OrderByDescending(x => x));
        Assert.Equal(new[] { 7L }, PythonGroupMoveScan.LiteralIds("ElementId(System.Int64(7))"));
    }

    [Fact]
    public void Only_referenced_members_that_kept_their_place_are_reported()
    {
        var before = new Dictionary<long, double[]> { [1] = P(0, 0, 0), [2] = P(5, 5, 5), [3] = P(1, 1, 1) };
        var after = new Dictionary<long, double[]> { [1] = P(0, 0, 0), [2] = P(5, 5, 4.99), [3] = P(1, 1, 1) };
        Assert.Equal(new[] { 1L }, PythonGroupMoveScan.Unmoved(new long[] { 1, 2, 99 }, before, after, 1e-6));
    }

    [Fact]
    public void Execute_python_watches_moves_before_the_rehearsal_rolls_back()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "ExecutePythonCommand.cs");
        int capture = s.IndexOf("groupMoves?.Capture(doc);", StringComparison.Ordinal);
        int rollback = s.IndexOf("rehearsalTracker?.StopBeforeRollback();", StringComparison.Ordinal);
        Assert.True(capture > 0 && rollback > capture);
        Assert.Contains("PythonGroupMoveWatch.Start(doc, runCode)", s);
        Assert.Contains("response[\"group_member_moves\"] = groupMoves.Report(runCode, outputWhole)", s);
    }

    // ---------------------------------------------------------------- point 4
    [Fact]
    public void A_rotated_instance_is_placed_by_a_rigid_fit()
    {
        var reference = new List<double[]> { P(0, 0, 3), P(2, 0, 3), P(2, 1, 3) };
        // 90 degrees about Z, then (10, 5, 0)
        var instance = reference.Select(q => P(-q[1] + 10, q[0] + 5, q[2])).ToList();
        PlanarPlacement f = PlanarFit.Fit(reference, instance);
        Assert.NotNull(f);
        Assert.False(f.Mirrored);
        Assert.Equal(90, f.AngleDegrees, 6);
        Assert.True(f.ResidualFeet < 1e-9);
        double[] v = f.ApplyVector(P(0, 0, -3 / 304.8));
        Assert.Equal(0, v[0], 9); Assert.Equal(0, v[1], 9); Assert.Equal(-3 / 304.8, v[2], 12);
        double[] w = f.ApplyVector(P(1, 0, 0));
        Assert.Equal(0, w[0], 9); Assert.Equal(1, w[1], 9);
    }

    [Fact]
    public void A_mirrored_instance_is_fitted_as_a_reflection()
    {
        var reference = new List<double[]> { P(0, 0, 0), P(3, 0, 0), P(3, 2, 0), P(0, 1, 1) };
        var instance = reference.Select(q => P(q[0] + 20, -q[1], q[2])).ToList();
        PlanarPlacement f = PlanarFit.Fit(reference, instance);
        Assert.True(f.Mirrored);
        Assert.True(f.ResidualFeet < 1e-9);
    }

    [Fact]
    public void An_instance_that_is_not_a_rigid_copy_shows_its_residual()
    {
        var reference = new List<double[]> { P(0, 0, 0), P(3, 0, 0), P(3, 2, 0) };
        var instance = new List<double[]> { P(10, 0, 0), P(13, 0, 0), P(13, 2.5, 0) };
        Assert.True(PlanarFit.Fit(reference, instance).ResidualFeet > 0.1);
        Assert.Null(PlanarFit.Fit(reference, new List<double[]>()));
    }

    private static EditMember M(long id, string key, params double[][] pts) => new EditMember { Id = id, Key = key, Points = pts.ToList() };

    [Fact]
    public void Members_pair_by_order_only_when_every_position_agrees()
    {
        var r = new List<EditMember> { M(1, "pipe", P(0, 0, 0), P(1, 0, 0)), M(2, "line"), M(3, "elbow", P(0, 0, 0)) };
        var ok = new List<EditMember> { M(11, "pipe", P(5, 0, 0), P(6, 0, 0)), M(12, "line"), M(13, "elbow", P(5, 0, 0)) };
        Assert.Equal(new[] { 0, 1, 2 }, GroupEditMatching.ByOrder(r, ok, out _));
        var swapped = new List<EditMember> { ok[0], ok[2], ok[1] };
        Assert.Null(GroupEditMatching.ByOrder(r, swapped, out string why));
        Assert.Contains("position 1", why);
        Assert.Null(GroupEditMatching.ByOrder(r, ok.Take(2).ToList(), out why));
        Assert.Contains("holds 2", why);
    }

    [Fact]
    public void Geometry_matching_takes_the_nearest_same_key_member_and_reversed_curves()
    {
        var expected = new List<EditMember> { M(1, "elbow", P(0, 0, 0)), M(2, "elbow", P(5, 0, 0)), M(3, "pipe", P(0, 0, 0), P(5, 0, 0)) };
        var actual = new List<EditMember> { M(21, "pipe", P(5, 0, 0), P(0, 0, 0)), M(22, "elbow", P(5, 0, 0.0001)), M(23, "elbow", P(0, 0, 0)) };
        var map = GroupEditMatching.ByGeometry(expected, actual, 0.001);
        Assert.Equal(2, map[0]); Assert.Equal(1, map[1]); Assert.Equal(0, map[2]);
        Assert.Empty(GroupEditMatching.ByGeometry(expected, new List<EditMember> { M(9, "elbow", P(9, 9, 9)) }, 0.001));
        double[] off = GroupEditMatching.MeanOffset(new List<EditMember> { M(1, "a", P(0, 0, 0)) }, new List<EditMember> { M(2, "a", P(1, 2, 3)) }, new Dictionary<int, int> { [0] = 0 });
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, off);
    }

    [Fact]
    public void An_external_connection_is_reconnected_stretched_or_reported_lost()
    {
        double mm = 1 / 304.8;
        var downpipe = new ExternalConnection { ExternalIsCurve = true, ExternalEndOfCurve = true };
        Assert.Equal(ExternalReconnectRules.Connect, ExternalReconnectRules.Decide(downpipe, 0.0, 50 * mm, 0.5 * mm, out _));
        Assert.Equal(ExternalReconnectRules.StretchAndConnect, ExternalReconnectRules.Decide(downpipe, 3 * mm, 50 * mm, 0.5 * mm, out _));
        Assert.Equal(ExternalReconnectRules.Lost, ExternalReconnectRules.Decide(downpipe, 80 * mm, 50 * mm, 0.5 * mm, out string why));
        Assert.Contains("beyond the stretch limit of 50 mm", why);
        var fitting = new ExternalConnection { ExternalIsCurve = false };
        Assert.Equal(ExternalReconnectRules.Lost, ExternalReconnectRules.Decide(fitting, 3 * mm, 50 * mm, 0.5 * mm, out why));
        Assert.Contains("not a pipe/duct end", why);
        Assert.Equal(ExternalReconnectRules.Connect, ExternalReconnectRules.Decide(fitting, 0.1 * mm, 50 * mm, 0.5 * mm, out _));
        Assert.Equal(ExternalReconnectRules.Lost, ExternalReconnectRules.Decide(downpipe, null, 50 * mm, 0.5 * mm, out _));
        Assert.Equal(ExternalReconnectRules.RemovedWithMember, ExternalReconnectRules.Decide(new ExternalConnection { MemberRemoved = true }, 0.0, 1, 1, out _));
        // remove/add_members reconnect only by coincidence: a limit of 0 never stretches.
        Assert.Equal(ExternalReconnectRules.Lost, ExternalReconnectRules.Decide(downpipe, 3 * mm, 0, 0.5 * mm, out _));
    }

    [Fact]
    public void Edit_type_members_runs_in_one_transaction_group_and_rolls_back_on_any_failed_check()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.EditType.cs");
        Assert.Contains("new TransactionGroup(doc, txName)", s);
        Assert.Contains("Guard.RollBack(group)", s);
        Assert.Contains("((Group)doc.GetElement(refState.GroupId)).UngroupMembers()", s);
        Assert.Contains("doc.Create.NewGroup(refState.MembersBefore)", s);
        Assert.Contains("g.GroupType = made.GroupType;", s);
        Assert.Contains("\"edit_type_members_verify\"", s);   // the fault the live rollback test injects
        Assert.Contains("scope=type", s);
        string m = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.Execution.cs");
        Assert.Contains("if (op == \"edit_type_members\") return ExecuteEditType(app, request);", m);
    }

    [Fact]
    public void Remove_members_takes_dependents_along_and_names_the_failing_instance()
    {
        string m = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.cs");
        string execution = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.Execution.cs");
        Assert.Contains("DependentMembers(doc, p.Elements, current)", m);
        Assert.Contains("ReconnectRetained(doc, p, o, originalId);", m);
        Assert.Contains("\"external_connections\"", m);
        Assert.Contains("RehearseCommitted(doc, p, name)", execution);
        Assert.Contains("Revit did not regenerate this instance onto the redefined type", m);
        Assert.Contains("parts.Add(name + \" - \" + bad.Count + \" failing (\"", execution);
    }

    // ---------------------------------------------------------- points 3, 4, 6, 7: contract
    private static CommandContract C(string name) => Contract.All.First(c => c.Name == name);

    [Fact]
    public void Manage_groups_publishes_edit_type_members_and_says_this_instance_splits_the_type()
    {
        CommandContract c = C("horizun_manage_groups");
        Assert.Contains("edit_type_members", c.InputSchema.SelectToken("properties.operation.enum").Values<string>());
        Assert.Equal(new[] { "all_instances", "this_instance", "type" }, c.InputSchema.SelectToken("properties.scope.enum").Values<string>());
        string scopeDescription = (string)c.InputSchema.SelectToken("properties.scope.description");
        Assert.Contains("type is an alias for all_instances", scopeDescription);
        Assert.Contains("this_instance requires a new type name", scopeDescription);
        Assert.Contains("floor split", c.Description);
        Assert.Contains("move|set_curve|set_parameter", (string)c.InputSchema.SelectToken("properties.edits.description"));
        Assert.NotNull(c.InputSchema.SelectToken("properties.accept_lost_connections"));
        Assert.Contains("edit_type_members", c.Description);
        Assert.NotNull(c.InputSchema.SelectToken("properties.idempotency_key"));
        Assert.NotNull(c.InputSchema.SelectToken("properties.confirmation_token"));
        Assert.True((bool)c.InputSchema.SelectToken("properties.dry_run.default"));
    }

    [Fact]
    public void Level_span_exception_is_one_argument_of_the_call_not_a_model_parameter()
    {
        CommandContract c = C("horizun_create_elements");
        Assert.Equal("string", (string)c.InputSchema.SelectToken("properties.level_span_exception.type"));
        Assert.Contains("no model parameter", (string)c.InputSchema.SelectToken("properties.level_span_exception.description"));
        // It is not a row field: a row that carries it is refused rather than ignored.
        Assert.NotNull(ToolInputRules.ValidateCreation(new JObject { ["kind"] = "pipe", ["level_span_exception"] = "x" }, "pipe"));
    }
    [Fact]
    public void The_level_span_rule_reads_the_argument_and_reports_it()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "CreateElementsLevelSpan.cs");
        Assert.Contains("string declared = batchException;", s);
        Assert.Contains("no model parameter needed", s);
        string c = Source("src", "Horizun.Revit", "Commands", "CreateElementsCommand.Prepare.cs") +
            Source("src", "Horizun.Revit", "Commands", "CreateElementsCommand.Execution.cs");
        Assert.Contains("LevelSpanCreationError(doc, plan, request.Value<string>(\"level_span_exception\"))", c);
        Assert.Contains("[\"declared_exceptions\"] = DeclaredExceptions(plans)", c);
        Assert.Contains("appliedData[\"declared_exceptions\"] = declared", c);
    }

    [Fact]
    public void A_declared_exception_reaches_the_action_history_receipt()
    {
        var reply = new JObject { ["declared_exceptions"] = new JArray(new JObject { ["index"] = 0, ["reason"] = "bajante continua" }) };
        JObject receipt = ReceiptLedger.Build("horizun_create_elements", true, null, reply, 0, 1, "1", DateTime.UtcNow);
        Assert.Equal("bajante continua", (string)receipt["declared_exceptions"][0]["reason"]);
        JObject none = ReceiptLedger.Build("horizun_create_elements", true, null, new JObject { ["declared_exceptions"] = new JArray() }, 0, 1, "1", DateTime.UtcNow);
        Assert.Null(none["declared_exceptions"]);
    }

    [Fact]
    public void Horizun_target_publishes_focus_window()
    {
        CommandContract c = C("horizun_target");
        Assert.Equal(new[] { "report", "focus_window" }, c.InputSchema.SelectToken("properties.operation.enum").Values<string>());
        string s = Source("src", "Horizun.Server", "WindowFocus.cs");
        Assert.Contains("AttachThreadInput", s);
        Assert.Contains("GetForegroundWindow", s);
        Assert.DoesNotContain("keybd_event", s);
        Assert.DoesNotContain("SendInput", s);
    }

    // ---------------------------------------------------------------- point 5
    private sealed class Cache : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "hz-cloud-" + Guid.NewGuid().ToString("N"));
        public string Put(string account, string project, string model, int bytes)
        {
            string dir = Path.Combine(Root, account, project);
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, model + ".rvt");
            File.WriteAllBytes(f, new byte[bytes]);
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
            return f;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private const string Project = "1f2e3d4c-0000-4000-8000-00000000abcd";
    private const string Model = "9a8b7c6d-1111-4111-8111-00000000ef01";

    [Fact]
    public void The_cache_root_follows_the_revit_year()
        => Assert.Equal(Path.Combine(@"C:\L", "Autodesk", "Revit", "Autodesk Revit 2026", "CollaborationCache"), CloudCacheLocator.CacheRoot(@"C:\L", "2026"));

    [Fact]
    public void A_save_into_the_current_accounts_copy_is_measured_by_time_and_size()
    {
        using (var c = new Cache())
        {
            string f = c.Put("USERA1", Project, Model, 100);
            var before = CloudCacheLocator.Find(c.Root, "usera1", Project.ToUpperInvariant(), Model);
            Assert.Single(before);
            Assert.True(before[0].CurrentAccount);
            File.WriteAllBytes(f, new byte[140]);
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
            CloudSaveVerdict v = CloudCacheLocator.Judge(before, CloudCacheLocator.Find(c.Root, "usera1", Project, Model), "usera1");
            Assert.Equal(f, v.Path);
            Assert.True(v.Changed);
            Assert.False(v.AccountMismatch);
            Assert.Null(v.Warning);
        }
    }

    [Fact]
    public void An_untouched_copy_is_not_a_save()
    {
        using (var c = new Cache())
        {
            c.Put("USERA1", Project, Model, 100);
            var before = CloudCacheLocator.Find(c.Root, "USERA1", Project, Model);
            CloudSaveVerdict v = CloudCacheLocator.Judge(before, CloudCacheLocator.Find(c.Root, "USERA1", Project, Model), "USERA1");
            Assert.False(v.Changed);
        }
    }

    [Fact]
    public void A_copy_only_under_another_account_warns_that_the_autodesk_user_changed()
    {
        using (var c = new Cache())
        {
            string f = c.Put("OLDUSER", Project, Model, 100);
            var before = CloudCacheLocator.Find(c.Root, "NEWUSER", Project, Model);
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
            CloudSaveVerdict v = CloudCacheLocator.Judge(before, CloudCacheLocator.Find(c.Root, "NEWUSER", Project, Model), "NEWUSER");
            Assert.True(v.AccountMismatch);
            Assert.Contains("'OLDUSER'", v.Warning);
            Assert.Contains("user changed between sessions", v.Warning);
        }
    }

    [Fact]
    public void Two_account_folders_judge_the_current_one_and_say_so()
    {
        using (var c = new Cache())
        {
            c.Put("OLDUSER", Project, Model, 100);
            string mine = c.Put("NEWUSER", Project, Model, 100);
            var before = CloudCacheLocator.Find(c.Root, "NEWUSER", Project, Model);
            Assert.Equal(2, before.Count);
            Assert.True(before[0].CurrentAccount);
            File.SetLastWriteTimeUtc(mine, new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc));
            CloudSaveVerdict v = CloudCacheLocator.Judge(before, CloudCacheLocator.Find(c.Root, "NEWUSER", Project, Model), "NEWUSER");
            Assert.Equal(mine, v.Path);
            Assert.True(v.Changed);
            Assert.True(v.SeveralAccounts);
            Assert.Contains("2 account folders", v.Warning);
        }
    }

    [Fact]
    public void A_copy_created_by_the_save_counts_and_a_missing_one_is_said()
    {
        using (var c = new Cache())
        {
            Directory.CreateDirectory(c.Root);
            var before = CloudCacheLocator.Find(c.Root, "U", Project, Model);
            Assert.Empty(before);
            CloudSaveVerdict none = CloudCacheLocator.Judge(before, before, "U");
            Assert.Null(none.Path);
            Assert.Contains("no cached copy", none.Warning);
            c.Put("U", Project, Model, 10);
            CloudSaveVerdict made = CloudCacheLocator.Judge(before, CloudCacheLocator.Find(c.Root, "U", Project, Model), "U");
            Assert.True(made.Changed);
            Assert.Empty(CloudCacheLocator.Find(Path.Combine(c.Root, "nope"), "U", Project, Model));
        }
    }

    [Fact]
    public void Save_local_measures_the_cache_copy_of_a_cloud_model()
    {
        string s = Source("src", "Horizun.Revit", "Commands", "SaveDocumentCommand.cs");
        Assert.Contains("doc.IsModelInCloud", s);
        Assert.Contains("mp.GetProjectGUID()", s);
        Assert.Contains("mp.GetModelGUID()", s);
        Assert.Contains("app.Application.LoginUserId", s);
        Assert.Contains("[\"cloud_cache\"]", s);
    }
}
