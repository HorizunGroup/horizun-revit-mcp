using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires native Win32 dialogs.";
        }
    }

    public class ModalAutoCloseWin32Tests
    {
        [WindowsTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void Posted_cancel_is_an_attempt_until_the_dialog_disappears(bool acceptsCancel)
        {
            using var dialog = new TestDialog(acceptsCancel);
            var className = new StringBuilder(64);
            GetClassName(dialog.Handle, className, className.Capacity);
            Assert.Equal("#32770", className.ToString());
            var info = new ModalDialogInfo { Handle = dialog.Handle, DialogWindowFound = true,
                Title = "External Tool Failure", ClassName = className.ToString() };
            var decision = ModalAutoClose.Decide(info);
            var attempts = new JArray();
            var closedDialogs = new JArray();
            var attempt = ModalAutoClose.Attempt(info, decision, "test", () =>
                Assert.True(SpinWait.SpinUntil(() => dialog.Received, TimeSpan.FromSeconds(5))), attempts, closedDialogs);
            Assert.NotNull(attempt);
            Assert.Single(attempts);
            Assert.Same(attempt, attempts[0]);
            Assert.Equal(acceptsCancel ? 1 : 0, closedDialogs.Count);
            if (acceptsCancel) Assert.True(JToken.DeepEquals(attempt, closedDialogs[0]));
            Assert.NotNull(attempt["attempted_utc"]);
            Assert.Equal(acceptsCancel, (bool)attempt["closed_verified"]);
            Assert.Equal(acceptsCancel, !IsWindow(dialog.Handle));
            Assert.Equal(acceptsCancel, attempt["closed_utc"] != null);
            Assert.Null(attempt["model_changed"]);
        }

        private sealed class TestDialog : IDisposable
        {
            private readonly Thread thread;
            private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
            private volatile bool stop, received;
            private readonly bool accepts;
            private readonly DialogProc callback;
            public IntPtr Handle { get; private set; }
            public bool Received => received;
            public TestDialog(bool accepts)
            {
                this.accepts = accepts;
                callback = OnDialog;
                thread = new Thread(Run) { IsBackground = true };
                thread.Start();
                if (!ready.Wait(TimeSpan.FromSeconds(5)) || Handle == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the private test dialog.");
            }
            private IntPtr OnDialog(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam)
            {
                if (msg == 0x111 && wparam.ToInt64() == 2)
                {
                    if (accepts) DestroyWindow(hwnd);
                    received = true;
                    return new IntPtr(1);
                }
                return IntPtr.Zero;
            }
            private void Run()
            {
                IntPtr template = Marshal.AllocHGlobal(24);
                try
                {
                    for (int i = 0; i < 24; i++) Marshal.WriteByte(template, i, 0);
                    Marshal.WriteInt32(template, unchecked((int)0x80000000)); // WS_POPUP, hidden
                    Handle = CreateDialogIndirectParam(IntPtr.Zero, template, IntPtr.Zero, callback, IntPtr.Zero);
                    ready.Set();
                    while (!stop)
                    {
                        while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, 1))
                        { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                        Thread.Sleep(1);
                    }
                    if (IsWindow(Handle)) DestroyWindow(Handle);
                }
                finally { ready.Set(); Marshal.FreeHGlobal(template); }
            }
            public void Dispose() { stop = true; Assert.True(thread.Join(5000)); ready.Dispose(); }
        }
        private delegate IntPtr DialogProc(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; public uint privateData; }
        [DllImport("user32.dll", EntryPoint = "CreateDialogIndirectParamW")]
        private static extern IntPtr CreateDialogIndirectParam(IntPtr instance, IntPtr template, IntPtr parent, DialogProc proc, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    }
}
