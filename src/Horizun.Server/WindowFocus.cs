// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_target operation=focus_window: bring ONE Revit instance's main window to
// the front, by process id. With several Revit open, nothing else says which
// window belongs to which pid, and a person asked to "look at the model" looks at
// whichever one happens to be in front.
//
// WINDOWS DECIDES, NOT THIS CODE. SetForegroundWindow is honoured only for a
// process that may set the foreground (the foreground lock): a background process
// such as this server, started by an MCP client, is usually refused - the call
// returns and the taskbar button flashes instead. So the steps are, in order and
// each re-checked with GetForegroundWindow:
//   1. restore it if minimised, then SetForegroundWindow;
//   2. AttachThreadInput to the current foreground thread (the documented way a
//      thread shares the foreground right), BringWindowToTop, SetForegroundWindow;
//   3. a topmost/no-topmost toggle, which lifts it above the others even when the
//      lock still keeps the keyboard focus elsewhere.
// No keystroke is ever injected: the usual ALT-press trick would open Revit's
// ribbon key tips in the user's session. The reply says which step held and
// whether the window IS the foreground window now - verified, never assumed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class WindowFocus
    {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        private const int SW_RESTORE = 9;
        private const uint GW_OWNER = 4, GA_ROOTOWNER = 3;
        private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_SHOWWINDOW = 0x40;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);

        public static JObject Bring(int pid)
        {
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { throw new ToolRefusal("No process with id " + pid + " is running. Nothing was focused."); }
            string name;
            try { name = process.ProcessName; } catch { name = null; }
            if (!string.Equals(name, "Revit", StringComparison.OrdinalIgnoreCase))
                throw new ToolRefusal("Process " + pid + " is '" + name + "', not Revit. focus_window only brings a Revit window forward. Nothing was focused.");

            IntPtr hwnd = MainWindow(process, pid);
            if (hwnd == IntPtr.Zero)
                throw new ToolRefusal("Revit process " + pid + " has no visible main window yet (still starting, or a dialog owns the screen). Nothing was focused.");

            var steps = new JArray();
            JObject before = Describe(GetForegroundWindow());
            if (IsIconic(hwnd)) { ShowWindow(hwnd, SW_RESTORE); steps.Add("restored from minimised"); }

            bool done = TryStep(hwnd, steps, "SetForegroundWindow", () => SetForegroundWindow(hwnd));
            if (!done) done = TryStep(hwnd, steps, "AttachThreadInput + BringWindowToTop + SetForegroundWindow", () =>
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
                uint me = GetCurrentThreadId();
                bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
                try { BringWindowToTop(hwnd); return SetForegroundWindow(hwnd); }
                finally { if (attached) AttachThreadInput(me, fgThread, false); }
            });
            if (!done) done = TryStep(hwnd, steps, "topmost toggle + SetForegroundWindow", () =>
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                return SetForegroundWindow(hwnd);
            });
            if (!done)
            {
                var flash = new FlashInfo { cbSize = (uint)Marshal.SizeOf(typeof(FlashInfo)), hwnd = hwnd, dwFlags = 3 | 12, uCount = 3, dwTimeout = 0 };
                FlashWindowEx(ref flash);
                steps.Add("taskbar button flashed (Windows kept the foreground lock)");
            }
            JObject after = Describe(GetForegroundWindow());
            return new JObject
            {
                ["operation"] = "focus_window",
                ["pid"] = pid,
                ["window"] = Describe(hwnd),
                ["foreground_before"] = before,
                ["foreground_after"] = after,
                ["verified_foreground"] = done,
                ["steps"] = steps,
                ["target_changed"] = false,
                ["note"] = done
                    ? "GetForegroundWindow re-read after the step names this Revit's main window. The tools' target was NOT changed; pass pid without operation to change it."
                    : "Windows refused to hand the foreground to a background process (foreground lock). The window was raised and its taskbar button flashed; a click on it completes the switch. Not reported as focused."
            };
        }

        private static bool TryStep(IntPtr hwnd, JArray steps, string name, Func<bool> act)
        {
            bool returned;
            try { returned = act(); } catch { returned = false; }
            for (int i = 0; i < 10; i++)
            {
                if (IsForeground(hwnd)) { steps.Add(name + ": foreground verified"); return true; }
                Thread.Sleep(50);
            }
            steps.Add(name + ": " + (returned ? "returned true but the window is not the foreground window" : "refused"));
            return false;
        }

        private static bool IsForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return true;
            // A Revit child dialog in front still means this Revit is in front.
            return fg != IntPtr.Zero && GetAncestor(fg, GA_ROOTOWNER) == hwnd;
        }

        /// <summary>The process's main window: Process.MainWindowHandle when it is a visible unowned window, else the largest visible unowned top-level window of the pid.</summary>
        private static IntPtr MainWindow(Process process, int pid)
        {
            IntPtr main = IntPtr.Zero;
            try { process.Refresh(); main = process.MainWindowHandle; } catch { }
            if (main != IntPtr.Zero && IsWindowVisible(main) && GetWindow(main, GW_OWNER) == IntPtr.Zero) return main;
            IntPtr best = IntPtr.Zero; long bestArea = -1;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (owner != (uint)pid || !IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if (!GetWindowRect(h, out Rect r)) return true;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static JObject Describe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            var title = new StringBuilder(512); GetWindowText(hwnd, title, 512);
            var cls = new StringBuilder(128); GetClassName(hwnd, cls, 128);
            return new JObject { ["hwnd"] = hwnd.ToInt64(), ["pid"] = (long)pid, ["title"] = title.ToString(), ["class"] = cls.ToString() };
        }
    }
}
