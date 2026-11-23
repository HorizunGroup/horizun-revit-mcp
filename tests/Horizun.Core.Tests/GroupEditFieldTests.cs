// edit_type_members after the field report (Revit 2024, a cloud workshared model, a group type
// with ~1,000 members x 29 instances): size budget, offset-preserving reconnection, the
// connection ledger (internal links checked after the edit), drag and slope reporting, the
// grouped failure digest, and the source seams that wire them in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public sealed class GroupEditFieldTests
{
    private const double Mm = 1 / 304.8;
    private static double[] P(double x, double y, double z) => new[] { x * Mm, y * Mm, z * Mm };

    private static string Source(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(new[] { root.FullName }.Concat(parts).ToArray()));
    }

    // ------------------------------------------------------------- P1 budget
    [Fact]
    public void The_field_type_is_refused_synchronously_and_admitted_as_a_job()
    {
        Assert.Equal(29638, GroupEditBudget.MemberInstances(29, 1022));
        Assert.True(GroupEditBudget.NeedsAsync(29, 1022, runningAsJob: false));
        Assert.False(GroupEditBudget.NeedsAsync(29, 1022, runningAsJob: true));
        Assert.False(GroupEditBudget.NeedsAsync(29, 164, runningAsJob: false));
        int[] s = GroupEditBudget.EstimatedSeconds(29, 1022);
        Assert.True(s[0] < 600 && s[1] > 1000, "the field run took ~1,260 s; the range must contain it");
        Assert.True(GroupEditBudget.EstimatedSeconds(29, 207)[1] < 600, "anything admitted synchronously must fit the 600 s limit at the slow rate");
    }

    // ------------------------------------------------- P2/P4 reconnection
    [Fact]
    public void An_offset_the_connection_already_had_is_kept_not_absorbed()
    {
        // connected 0.702 mm apart; the member did not move: connect where it is, no stretch
        var c = new ExternalConnection { ExternalIsCurve = true, ExternalEndOfCurve = true, Point = P(0, 0, 0), ExternalPoint = P(0.702, 0, 0) };
        double[] desired = PreservingReconnect.Desired(c.ExternalPoint, c.Point, P(0, 0, 0));
        Assert.Equal(0, GroupEditMatching.D(desired, c.ExternalPoint), 9);
        Assert.Equal(ExternalReconnectRules.Connect, PreservingReconnect.Decide(c, 0, 5 * Mm, 0.5 * Mm, out _));
    }

    [Fact]
    public void An_8_mm_offset_survives_when_its_member_did_not_move()
    {
        // field: two connections 8.339 mm apart were lost against a 5 mm stretch limit
        var c = new ExternalConnection { ExternalIsCurve = true, ExternalEndOfCurve = true, Point = P(0, 0, 0), ExternalPoint = P(8.339, 0, 0) };
        double[] desired = PreservingReconnect.Desired(c.ExternalPoint, c.Point, P(0, 0, 0));
        double residual = GroupEditMatching.D(desired, P(8.339, 0, 0));
        Assert.Equal(ExternalReconnectRules.Connect, PreservingReconnect.Decide(c, residual, 5 * Mm, 0.5 * Mm, out _));
        // the old rule measured the gap to the member and lost it
        Assert.Equal(ExternalReconnectRules.Lost, ExternalReconnectRules.Decide(c, 8.339 * Mm, 5 * Mm, 0.5 * Mm, out _));
    }

    [Fact]
    public void A_moved_member_drags_the_external_end_by_its_own_movement_only()
    {
        var c = new ExternalConnection { ExternalIsCurve = true, ExternalEndOfCurve = true, Point = P(0, 0, 0), ExternalPoint = P(1.72, 0, 0) };
        double[] desired = PreservingReconnect.Desired(c.ExternalPoint, c.Point, P(0, 0, -3));
        Assert.Equal(1.72 * Mm, desired[0], 9);
        Assert.Equal(-3 * Mm, desired[2], 9);
        double residual = GroupEditMatching.D(desired, c.ExternalPoint);
        Assert.Equal(3.0, residual / Mm, 6);      // not 3.443 (= the gap to the member)
        Assert.Equal(ExternalReconnectRules.StretchAndConnect, PreservingReconnect.Decide(c, residual, 5 * Mm, 0.5 * Mm, out _));
        var fitting = new ExternalConnection { ExternalIsCurve = false };
        Assert.Equal(ExternalReconnectRules.Lost, PreservingReconnect.Decide(fitting, residual, 5 * Mm, 0.5 * Mm, out string why));
        Assert.Contains("not a pipe/duct end", why);
        Assert.Equal(ExternalReconnectRules.Lost, PreservingReconnect.Decide(c, null, 5 * Mm, 0.5 * Mm, out _));
    }

    // ------------------------------------------------------- P3 ledger
    private static ConnectionLedger.End M(int inst, int member, int conn) => new ConnectionLedger.End { Instance = inst, Member = member, Connector = conn };
    private static ConnectionLedger.End X(long id, int conn) => new ConnectionLedger.End { ElementId = id, Connector = conn };

    [Fact]
    public void A_connection_is_captured_once_whichever_end_saw_it_and_classified()
    {
        var l = new ConnectionLedger();
        Assert.True(l.Add(M(0, 1, 2), M(0, 5, 1), 0));
        Assert.False(l.Add(M(0, 5, 1), M(0, 1, 2), 0));
        Assert.True(l.Add(M(3, 0, 1), X(900, 0), 0));
        Assert.True(l.Add(X(900, 1), X(901, 0), 0));
        Assert.True(l.Add(M(1, 0, 3), M(2, 0, 2), 0));
        Assert.Equal(4, l.Count);
        Assert.Equal(new[] { "between_instances", "external", "internal", "neighbour" }, l.Links.Select(x => x.Kind).OrderBy(k => k));
    }

    [Fact]
    public void An_internal_link_taken_apart_fails_although_every_external_one_held()
    {
        var l = new ConnectionLedger();
        l.Add(M(0, 1, 2), M(0, 5, 1), 0);           // tee to 2" branch, instance 0
        l.Add(M(4, 1, 2), M(4, 5, 1), 0);           // same link, instance 4
        l.Add(M(4, 0, 1), X(900, 0), 0);
        var v = l.Verify(link => !(link.A.Instance == 4 && link.Kind == "internal"), link => 0.0, 0.5 * Mm);
        Assert.Equal(1, v.ByKind["internal"].Lost);
        Assert.Equal(0, v.ByKind["external"].Lost);
        Assert.False(v.Holds("internal"));
        Assert.True(v.Holds("external"));
        Assert.Equal(4, v.ByKind["internal"].LostRows[0].A.Instance);
    }

    [Fact]
    public void A_link_still_connected_but_farther_apart_than_before_is_pulled_apart()
    {
        var l = new ConnectionLedger();
        l.Add(M(0, 1, 2), M(0, 5, 1), 0.702 * Mm);
        var same = l.Verify(_ => true, _ => 0.702 * Mm, 0.5 * Mm);
        Assert.True(same.Holds("internal"));
        var apart = l.Verify(_ => true, _ => 3.0 * Mm, 0.5 * Mm);
        Assert.Equal(1, apart.ByKind["internal"].PulledApart);
        Assert.False(apart.Holds("internal"));
        var gone = l.Verify(_ => null, _ => null, 0.5 * Mm);
        Assert.Equal(1, gone.ByKind["internal"].Lost);
    }

    // ------------------------------------------------ drag and slopes
    [Fact]
    public void Dragged_members_are_rigid_or_stretched_and_slopes_are_judged_on_runs()
    {
        var before = new EditMember { Points = { P(0, 0, 0), P(3000, 0, 30) } };
        var carried = new EditMember { Points = { P(0, 0, -3), P(3000, 0, 27) } };
        var tilted = new EditMember { Points = { P(0, 0, -3), P(3000, 0, 30) } };
        var dc = DragRules.Displacement(before, carried);
        Assert.True(DragRules.Rigid(dc, 0.01 * Mm));
        Assert.Equal(3.0, DragRules.Largest(dc) / Mm, 6);
        Assert.False(DragRules.Rigid(DragRules.Displacement(before, tilted), 0.01 * Mm));
        Assert.Null(DragRules.Displacement(before, new EditMember { Points = { P(0, 0, 0) } }));

        double? s0 = RunSlopeRules.Percent(before.Points[0], before.Points[1]);
        Assert.Equal(1.0, s0.Value, 6);
        Assert.False(RunSlopeRules.Changed(s0, RunSlopeRules.Percent(carried.Points[0], carried.Points[1])));
        Assert.True(RunSlopeRules.Changed(s0, RunSlopeRules.Percent(tilted.Points[0], tilted.Points[1])));
        Assert.True(RunSlopeRules.IsHorizontalRun(s0));
        Assert.Null(RunSlopeRules.Percent(P(0, 0, 0), P(0, 0, 3000)));          // a riser has no run
        Assert.False(RunSlopeRules.IsHorizontalRun(RunSlopeRules.Percent(P(0, 0, 0), P(300, 0, 300))));
    }

    // ------------------------------------------------- failure digest
    [Fact]
    public void Twelve_hundred_identical_failures_read_as_one_group_with_ids()
    {
        var d = new FailureDigest();
        for (int i = 0; i < 1202; i++)
        {
            string action = i < 1200 ? "rolled_back" : "resolved_detach";
            JObject sample = d.WantsSample("g-1", "Error", "Los elementos deben desconectarse.", action) ? new JObject { ["n"] = i } : null;
            d.Add("g-1", "Error", "Los elementos deben desconectarse.", action, new long[] { 1000 + i, 5000 + i % 41 }, new long[0], sample);
        }
        d.Add("g-2", "Warning", "w", "deleted_warning", new long[] { 7 }, new long[] { 8 }, null);
        JObject j = d.ToJson();
        Assert.Equal(1203, (int)j["total"]);
        var groups = (JArray)j["groups"];
        Assert.Equal(3, groups.Count);
        Assert.Equal(1200, (int)groups[0]["count"]);
        Assert.Equal(1241, (int)groups[0]["distinct_failing_elements"]);
        Assert.Equal(FailureDigest.SamplesPerGroup, ((JArray)groups[0]["samples"]).Count);
        Assert.Equal(new long[] { 1000, 5000 }, groups[0]["samples"][0]["failing_element_ids"].Select(t => (long)t));
        Assert.Equal(1202, d.CountOf("g-1"));
        Assert.Equal(2, d.CountOf("g-1", "resolved_detach"));
        Assert.Equal(1200, d.CountOf("g-1", "rolled_back"));
        Assert.Equal(new long[] { 8 }, groups[2]["samples"][0]["additional_element_ids"].Select(t => (long)t));
        Assert.False(d.WantsSample("g-1", "Error", "Los elementos deben desconectarse.", "rolled_back"));
    }

    [Fact]
    public void The_digest_bounds_its_groups()
    {
        var d = new FailureDigest();
        for (int i = 0; i < FailureDigest.MaxGroups + 5; i++) d.Add("g" + i, "Error", "t", "rolled_back", null, null, null);
        JObject j = d.ToJson();
        Assert.Equal(FailureDigest.MaxGroups, ((JArray)j["groups"]).Count);
        Assert.Equal(5, (int)j["not_grouped"]);
    }

    // ------------------------------------------------------- seams
    [Fact]
    public void The_edit_checks_size_ledger_and_resolves_disconnects_only_to_verify_them()
    {
        string src = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.EditType.cs");
        Assert.Contains("GroupEditBudget.NeedsAsync(plan.Instances.Count, perInstance, Job.Ambient != null)", src);
        Assert.Contains("\"route\"] = \"horizun_submit_job\"", src);
        Assert.Contains("failures.MayResolve = IsDisconnectFailure;", src);
        Assert.Contains("BuiltInFailures.ConnectorFailures.ElementsNeedToBeDisconnected", src);
        Assert.Contains("new PostconditionCheck(\"type\", \"instances\", \"external_connections\", \"connections\", \"parameters\")", src);
        Assert.Contains("PreservingReconnect.Desired(c.ExternalPoint, c.Point, A(mc.Origin))", src);
        Assert.Contains("CallProgress.Checkpoint(\"instance \"", src);
        Assert.Contains("[\"risk_non_coincident_before\"]", src);
        Assert.Contains("[\"dragged_members\"]", src);
        Assert.Contains("[\"external_side_effects\"]", src);
        // the resolution never happens with another error in the same pass
        string log = Source("src", "Horizun.Revit", "Commands", "ManageGroupsCommand.cs");
        Assert.Contains("bool onlyResolvable = messages.All(", log);
        Assert.Contains("FailureResolutionType.DetachElements", log);
        // remove/add_members (scope=all_instances) reconnect the same way: an offset is kept
        Assert.Contains("string decision = PreservingReconnect.Decide(c.Fact, residual, 0, EditTolerance, out string why);", log);
        Assert.Contains("ExternalPoint = A(r.Origin), GapBeforeFeet = c.Origin.DistanceTo(r.Origin) },", log);
    }

    // ------------------------------------------- concurrent MCP clients
    [Fact]
    public void A_write_warns_about_other_live_clients_but_not_about_exited_ones()
    {
        var presence = new ClientPresence();
        var t = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        presence.Seen(100, t);                 // another agent, still running
        presence.Seen(200, t.AddSeconds(5));   // a one-shot caller that has exited
        presence.Seen(300, t.AddSeconds(9));   // this call
        var others = ConcurrentClientRule.OthersAlive(presence.Take(t.AddSeconds(10)), pid => pid != 200);
        Assert.Equal(new long[] { 100 }, others);
        Assert.Contains("pid 100", ConcurrentClientRule.Warning(others));
        var alone = new ClientPresence(); alone.Seen(300, t);
        Assert.Empty(ConcurrentClientRule.OthersAlive(alone.Take(t), _ => true));
        Assert.Null(ConcurrentClientRule.Warning(new List<long>()));
        Assert.Contains("WarnConcurrentClients(req.Result)", Source("src", "Horizun.Revit", "Core", "Dispatcher.cs"));
    }

    [Fact]
    public void A_python_call_the_contract_rolls_back_is_not_a_write_that_landed()
    {
        // Live 2026-10-28 (battery 4.0): a read_only=true script carried "This write landed in a
        // model they can also change". Rolled-back runs write nothing; real Python writes still warn.
        Assert.True(ConcurrentClientRule.RolledBackByContract("horizun_execute_python", new JObject { ["read_only"] = true }));
        Assert.True(ConcurrentClientRule.RolledBackByContract("horizun_execute_python", new JObject { ["rehearsal"] = true }));
        Assert.False(ConcurrentClientRule.RolledBackByContract("horizun_execute_python", new JObject { ["code"] = "x" }));
        Assert.False(ConcurrentClientRule.RolledBackByContract("horizun_execute_python", new JObject { ["read_only"] = false }));
        Assert.False(ConcurrentClientRule.RolledBackByContract("horizun_manage_groups", new JObject { ["read_only"] = true }));
        Assert.Contains("RolledBackByContract(req.Name, request)", Source("src", "Horizun.Revit", "Core", "Dispatcher.cs"));
    }
}
