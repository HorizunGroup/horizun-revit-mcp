// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Whether Revit's UI thread is stuck on a modal dialog RIGHT NOW - asked from a
// thread that is not stuck (story 5.19).
//
// Interference sees dialogs raised WHILE a command runs on the UI thread. A
// modal that is already up BEFORE the call - the measured case was a "New
// Project" dialog - means the command never reaches the UI thread, so the one
// dialog that costs a caller its whole 600 s timeout is exactly the one the
// dialog watcher cannot see. The transport thread is not blocked, and Win32
// answers it: when a modal dialog is up, Revit's MAIN window is disabled and
// the dialog is the visible, enabled window on the same UI thread.
//
// Two facts are captured ONCE, on the UI thread, at startup - the main window
// handle and the UI thread's native id - because they are the two things a
// background thread cannot ask for itself.
//
// The probe never throws and never invents: a probe that could not look
// answers null ("no modal seen"), because failing a caller's request over a
// window this code could not actually observe would be the timeout lie again
// with a different alibi.
// -----------------------------------------------------------------------------
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Horizun.Revit.Core
{
    public static class ModalProbe
    {
        private static IntPtr _mainWindow;
        private static uint _uiThreadId;

        /// <summary>
        /// Call ONCE, ON THE UI THREAD, at add-in startup. The thread id has to be
        /// the native one (GetCurrentThreadId), because EnumThreadWindows speaks
        /// Win32, not managed thread ids.
        /// </summary>
        public static void CaptureUiThread(IntPtr mainWindowHandle)
        {
            _mainWindow = mainWindowHandle;
            _uiThreadId = GetCurrentThreadId();
        }

        /// <summary>Whether the probe was ever given its two facts.</summary>
        public static bool Available => _mainWindow != IntPtr.Zero && _uiThreadId != 0;

        /// <summary>
        /// Describe the modal dialog currently blocking the UI thread, or null when
        /// there is none - or when this probe cannot tell. Safe from any thread.
        /// </summary>
        public static string DescribeModal()
        {
            if (!Available) return null;
            try
            {
                if (!IsWindow(_mainWindow)) return null;
                // A modal dialog disables its owner. An ENABLED main window means no
                // modal, whatever else is open (modeless dialogs leave it enabled and
                // do not block the ExternalEvent).
                if (IsWindowEnabled(_mainWindow)) return null;

                string found = null;
                EnumThreadWindows(_uiThreadId, delegate (IntPtr hwnd, IntPtr _)
                {
                    if (hwnd == _mainWindow) return true;
                    if (!IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return true;

                    var title = new StringBuilder(512);
                    GetWindowText(hwnd, title, title.Capacity);
                    var cls = new StringBuilder(256);
                    GetClassName(hwnd, cls, cls.Capacity);

                    string t = title.ToString();
                    found = (t.Length > 0 ? "'" + t + "'" : "(an untitled window)") +
                            (cls.Length > 0 ? " [" + cls + "]" : "");
                    return false; // first visible enabled window on the thread is the one holding it
                }, IntPtr.Zero);

                // The main window being disabled IS the modal state, even when the
                // dialog itself could not be enumerated (it can live on another
                // thread of the same process, e.g. a splash or a shell dialog).
                return found ?? "(the Revit main window is disabled by a modal window this probe could not enumerate)";
            }
            catch
            {
                // Could not look. Null, never a guess - see the header.
                return null;
            }
        }

        private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
    }
}
