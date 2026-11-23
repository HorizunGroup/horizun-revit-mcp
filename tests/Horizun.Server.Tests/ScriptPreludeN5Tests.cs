// -----------------------------------------------------------------------------
// Horizun Server tests - 4.2-N5 (CT-P1-5e): the coordinate and leader helpers of
// the execute_python prelude, proven with stand-ins for the Revit objects.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ScriptPreludeN5Tests
    {
        private sealed class Job { public void Write(string l, object d, object t) { } }

        private const string Fakes = @"
class P(object):
    def __init__(self, x, y, z):
        self.X = x; self.Y = y; self.Z = z
def mk(t):
    return P(t[0], t[1], t[2])
class Tr(object):
    def OfPoint(self, p):
        return p
class Box(object):
    def __init__(self, lo, hi):
        self.Min = P(*lo); self.Max = P(*hi); self.Transform = Tr()
class View(object):
    # plan view looking down: right = +X, up = +Y, toward eye = +Z
    def __init__(self, scale=100, origin=(10.0, 20.0, 0.0), crop=((0.0, 0.0, -5.0), (100.0, 50.0, 5.0))):
        self.Origin = P(*origin); self.RightDirection = P(1.0, 0.0, 0.0)
        self.UpDirection = P(0.0, 1.0, 0.0); self.ViewDirection = P(0.0, 0.0, 1.0)
        self.Scale = scale; self.CropBox = Box(*crop)
class VP(object):
    def __init__(self, rot='None', center=(1.0, 0.5)):
        self.Rotation = rot; self._c = center
    def GetBoxCenter(self):
        return P(self._c[0], self._c[1], 0.0)
";

        private static ScriptScope Prepared(out ScriptEngine engine)
        {
            engine = Python.CreateEngine();
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("__hz_job", new Job());
            scope.SetVariable("__hz_raised", new Func<int, string>(_ => "[]"));
            scope.SetVariable("__hz_dialog_scope", new Func<string, IDisposable>(_ => null));
            engine.Execute(ScriptPrelude.Prologue, scope);
            engine.Execute(Fakes, scope);
            return scope;
        }

        [Fact]
        public void Model_view_and_sheet_round_trip_for_every_viewport_rotation()
        {
            foreach (string rot in new[] { "None", "Clockwise", "Counterclockwise" })
            {
                ScriptScope scope = Prepared(out ScriptEngine engine);
                scope.SetVariable("rot", rot);
                engine.Execute(@"
view = View(); vp = VP(rot)
pt = (40.0, 30.0, 2.0)
x, y = horizun.model_to_sheet(vp, pt, view, xyz=mk)
back = horizun.sheet_to_model(vp, x, y, 2.0, view, xyz=mk)
gap = max(abs(back[i] - pt[i]) for i in range(3))
", scope);
                Assert.True((double)scope.GetVariable("gap") < 1e-9, rot);
            }
        }

        [Fact]
        public void The_crop_centre_lands_on_the_viewport_box_centre_and_scale_divides()
        {
            ScriptScope scope = Prepared(out ScriptEngine engine);
            engine.Execute(@"
view = View(); vp = VP('None', (1.0, 0.5))
# crop centre in model = (50, 25); view origin (10, 20): u,v = (40, 5)
cx, cy = horizun.view_to_sheet(vp, 40.0, 5.0, view, xyz=mk)
# 100 feet to the right of the crop centre, at 1:100, is 1 foot right on the sheet
rx, ry = horizun.view_to_sheet(vp, 140.0, 5.0, view, xyz=mk)
", scope);
            Assert.Equal(1.0, (double)scope.GetVariable("cx"), 9);
            Assert.Equal(0.5, (double)scope.GetVariable("cy"), 9);
            Assert.Equal(2.0, (double)scope.GetVariable("rx"), 9);
        }

        [Fact]
        public void Calibration_returns_the_offset_that_makes_a_measured_point_exact()
        {
            ScriptScope scope = Prepared(out ScriptEngine engine);
            engine.Execute(@"
view = View(); vp = VP('None')
off = horizun.calibrate_viewport(vp, (60.0, 25.0, 0.0), (1.30, 0.62), view, xyz=mk)
x, y = horizun.model_to_sheet(vp, (60.0, 25.0, 0.0), view, off, xyz=mk)
", scope);
            Assert.Equal(1.30, (double)scope.GetVariable("x"), 9);
            Assert.Equal(0.62, (double)scope.GetVariable("y"), 9);
        }

        [Fact]
        public void Unknown_ray_direction_is_refused_and_into_view_runs_away_from_the_eye()
        {
            ScriptScope scope = Prepared(out ScriptEngine engine);
            engine.Execute(@"
view = View()
v = _hz_ray_vector(view, 'into_view')
try:
    _hz_ray_vector(view, 'sideways')
    refused = False
except ValueError:
    refused = True
", scope);
            Assert.Equal(-1.0, (double)((IronPython.Runtime.PythonTuple)scope.GetVariable("v"))[2], 9);
            Assert.True((bool)scope.GetVariable("refused"));
        }

        [Fact]
        public void Leaders_are_captured_restored_and_the_restore_is_verified_by_a_re_read()
        {
            ScriptScope scope = Prepared(out ScriptEngine engine);
            engine.Execute(@"
class Id(object):
    Value = 77
class Leader(object):
    def __init__(self, e, el):
        self.End = P(*e); self.Elbow = P(*el)
class Note(object):
    Id = Id()
    def __init__(self, ls): self._l = ls
    def GetLeaders(self): return self._l
note = Note([Leader((1.0, 2.0, 0.0), (0.5, 1.0, 0.0)), Leader((3.0, 3.0, 0.0), (2.0, 2.0, 0.0))])
cap = horizun.leaders_capture(note)
note.GetLeaders()[0].End = P(9.0, 9.0, 0.0)          # an edit moves a leader
rep = horizun.leaders_restore(note, cap, xyz=mk)
short = Note([Leader((0.0, 0.0, 0.0), (0.0, 0.0, 0.0))])
rep_short = horizun.leaders_restore(short, cap, xyz=mk)
class Stubborn(Leader):
    def __init__(self, e, el):
        object.__setattr__(self, 'End', P(*e)); object.__setattr__(self, 'Elbow', P(*el))
    def __setattr__(self, k, v):
        if k == 'End': return
        object.__setattr__(self, k, v)
stub = Note([Stubborn((5.0, 5.0, 0.0), (0.0, 0.0, 0.0)), Stubborn((6.0, 6.0, 0.0), (0.0, 0.0, 0.0))])
rep_stub = horizun.leaders_restore(stub, cap, xyz=mk)
cap_count = cap['count']
ok_verified = rep['verified']
short_restored = rep_short['restored']
short_reason = rep_short['reason']
stub_verified = rep_stub['verified']
", scope);
            Assert.Equal(2, (int)scope.GetVariable("cap_count"));
            Assert.True((bool)scope.GetVariable("ok_verified"));
            // Different count: nothing touched, said so.
            Assert.False((bool)scope.GetVariable("short_restored"));
            Assert.Equal("leader count differs", (string)scope.GetVariable("short_reason"));
            // A leader that refuses the write is reported, not assumed.
            Assert.False((bool)scope.GetVariable("stub_verified"));
        }
    }
}
