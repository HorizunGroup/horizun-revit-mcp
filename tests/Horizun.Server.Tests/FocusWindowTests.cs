// horizun_target operation=focus_window: what it refuses without touching any window.
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class FocusWindowTests
    {
        [Fact]
        public void Focus_window_needs_a_pid_and_no_year()
        {
            var noPid = Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["operation"] = "focus_window" }));
            Assert.Contains("needs pid", noPid.Message);
            var both = Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["operation"] = "focus_window", ["pid"] = 1, ["year"] = "2026" }));
            Assert.Contains("needs pid", both.Message);
        }

        [Fact]
        public void A_process_that_is_not_revit_is_never_focused()
        {
            int me = Process.GetCurrentProcess().Id;
            var r = Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["operation"] = "focus_window", ["pid"] = me }));
            Assert.Contains("not Revit", r.Message);
            Assert.Contains("Nothing was focused", r.Message);
        }

        [Fact]
        public void A_pid_that_is_not_running_is_refused()
        {
            var r = Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["operation"] = "focus_window", ["pid"] = int.MaxValue - 7 }));
            Assert.Contains("No process", r.Message);
        }

        [Fact]
        public void An_unknown_operation_changes_nothing()
        {
            var r = Assert.Throws<ToolRefusal>(() => Targets.Handle(new JObject { ["operation"] = "maximize" }));
            Assert.Contains("report", r.Message);
        }
    }
}
