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
//
// DescribeModalDetail() (added for horizun_health's blocked-UI-thread report)
// goes one step further than DescribeModal()'s single formatted line: once the
// dialog's own HWND is found, its CHILD windows are enumerated too, so the
// reply can NAME the dialog rather than just prove one is up - its title, a
// best-effort main text (the longest Static/SysLink child, which is a
// heuristic and says so), every Button child's caption in z-order, and the
// module that owns its window class when Win32 can resolve one. Every field
// here is DIRECTLY OBSERVED or explicitly null; nothing is inferred from the
// class name or the title. This code never clicks or posts to the dialog -
// it only reads.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Everything ModalProbe could directly observe about the modal window that
    /// disables Revit's main window right now. Every field is what Win32 actually
    /// returned for THIS window - null/empty means "could not read", never a guess.
    /// </summary>
    public sealed class ModalDialogInfo
    {
        /// <summary>GetWindowText of the dialog window itself. Empty for an untitled window.</summary>
        public string Title;
        /// <summary>GetClassName of the dialog window ("#32770" is the generic Windows dialog class).</summary>
        public string ClassName;
        /// <summary>
        /// Best-effort body text: the LONGEST non-empty text found on a direct Static
        /// or SysLink child. A dialog with several short labels (an icon caption, a
        /// checkbox) may have its real message text picked correctly by this heuristic
        /// or may not - AllText carries every candidate this probe found, in the order
        /// EnumChildWindows returned them, so a caller can judge for themselves.
        /// </summary>
        public string MainText;
        /// <summary>Every Static/SysLink child's text this probe could read, in enumeration order.</summary>
        public IReadOnlyList<string> AllText;
        /// <summary>Every Button child's caption, in enumeration (roughly left-to-right/top-to-bottom) order.</summary>
        public IReadOnlyList<string> Buttons;
        /// <summary>
        /// GetWindowModuleFileName for the dialog's window handle: the module (exe/dll)
        /// whose window class this is - Revit's own core, or a third-party add-in's
        /// dialog living on the same UI thread. Null when Win32 could not resolve it.
        /// </summary>
        public string OwningModule;
        /// <summary>True when the dialog's own HWND was found at all (false: only "the main window is disabled" is known).</summary>
        public bool DialogWindowFound;
        /// <summary>The dialog's HWND, for ModalProbe.TryCancel only. Zero when not found.</summary>
        internal IntPtr Handle;

        /// <summary>The single-line rendering DescribeModal() has always returned, built from these same facts.</summary>
        public string ToSummaryLine()
        {
            if (!DialogWindowFound)
                return "(the Revit main window is disabled by a modal window this probe could not enumerate)";
            string t = Title ?? "";
            return (t.Length > 0 ? "'" + t + "'" : "(an untitled window)") +
                   (!string.IsNullOrEmpty(ClassName) ? " [" + ClassName + "]" : "");
        }

        /// <summary>The structured evidence, for a CommandResult.FailWithDetail's structuredContent.</summary>
        public JObject ToJson()
        {
            return new JObject
            {
                ["dialog_window_found"] = DialogWindowFound,
                ["title"] = Title,
                ["class_name"] = ClassName,
                ["main_text"] = MainText,
                ["all_text"] = AllText == null ? new JArray() : new JArray(AllText),
                ["buttons"] = Buttons == null ? new JArray() : new JArray(Buttons),
                ["owning_module"] = OwningModule,
                ["summary"] = ToSummaryLine(),
                ["means"] = "Read-only Win32 evidence about the window currently blocking Revit's UI thread; " +
                            "nothing here was clicked or posted to. main_text is a heuristic - the longest " +
                            "Static/SysLink child - because a dialog can carry several short labels besides its " +
                            "real message; all_text carries every candidate this probe found, in enumeration " +
                            "order, so a caller can judge for themselves. A null field means Win32 could not " +
                            "read it, never that the dialog has no such thing."
            };
        }
    }

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
        /// Unchanged wording/behaviour from before DescribeModalDetail existed - built
        /// from the same underlying facts, so every existing caller keeps its exact text.
        /// </summary>
        public static string DescribeModal() => DescribeModalDetail()?.ToSummaryLine();

        /// <summary>
        /// The structured version: title, best-effort main text, every button caption
        /// and the owning module, for a caller (horizun_health's blocked-UI-thread
        /// report above all) that needs to NAME the dialog rather than just prove one
        /// is up. Null under exactly the same conditions DescribeModal() returns null:
        /// the probe was never given its facts, the main window handle has gone stale,
        /// or the main window is enabled (no modal). NEVER clicks or posts to anything
        /// it finds - read-only, always.
        /// </summary>
        public static ModalDialogInfo DescribeModalDetail()
        {
            if (!Available) return null;
            try
            {
                if (!IsWindow(_mainWindow)) return null;
                // A modal dialog disables its owner. An ENABLED main window means no
                // modal, whatever else is open (modeless dialogs leave it enabled and
                // do not block the ExternalEvent).
                if (IsWindowEnabled(_mainWindow)) return null;

                IntPtr dialog = IntPtr.Zero;
                EnumThreadWindows(_uiThreadId, delegate (IntPtr hwnd, IntPtr _)
                {
                    if (hwnd == _mainWindow) return true;
                    if (!IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return true;
                    dialog = hwnd;
                    return false; // first visible enabled window on the thread is the one holding it
                }, IntPtr.Zero);

                if (dialog == IntPtr.Zero)
                    // The main window being disabled IS the modal state, even when the
                    // dialog itself could not be enumerated (it can live on another
                    // thread of the same process, e.g. a splash or a shell dialog).
                    return new ModalDialogInfo { DialogWindowFound = false };

                return new ModalDialogInfo
                {
                    DialogWindowFound = true,
                    Handle = dialog,
                    Title = WindowText(dialog),
                    ClassName = ClassNameOf(dialog),
                    OwningModule = ModuleOf(dialog),
                    AllText = ChildTexts(dialog, out string longest, out List<string> buttons),
                    MainText = longest,
                    Buttons = buttons
                };
            }
            catch
            {
                // Could not look. Null, never a guess - see the header.
                return null;
            }
        }

        /// <summary>
        /// Every Static/SysLink child's text (candidates for the dialog's message body)
        /// and every Button child's caption, read directly from the dialog's own child
        /// windows - never from the dialog's title or class. Bounded (64 children) so a
        /// pathological window tree cannot turn a diagnostic read into a long scan.
        /// </summary>
        private static List<string> ChildTexts(IntPtr dialog, out string longest, out List<string> buttons)
        {
            var texts = new List<string>();
            var buttonList = new List<string>();
            string best = null;
            int seen = 0;
            EnumChildWindows(dialog, delegate (IntPtr child, IntPtr _)
            {
                if (++seen > 64) return false;   // bounded: this is a diagnostic read, not a full tree walk
                string cls = ClassNameOf(child) ?? "";
                string text = WindowText(child);
                if (string.IsNullOrEmpty(text)) return true;
                if (cls.Equals("Static", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("SysLink", StringComparison.OrdinalIgnoreCase))
                {
                    texts.Add(text);
                    if (best == null || text.Length > best.Length) best = text;
                }
                else if (cls.Equals("Button", StringComparison.OrdinalIgnoreCase))
                {
                    buttonList.Add(text);
                }
                return true;
            }, IntPtr.Zero);
            longest = best;
            buttons = buttonList;
            return texts;
        }

        /// <summary>
        /// THE ONE WRITE IN THIS CLASS, and only for the notices ModalAutoClose names
        /// (owner decision 2026-10-27). Posts IDCANCEL to the dialog - what Esc or the
        /// title-bar close does: "Cancel" on Revit's save reminder (nothing is saved,
        /// the model is untouched) and "Close" on External Tool Failure. Returns
        /// whether the message was posted; the caller re-probes to see it go.
        /// </summary>
        public static bool TryCancel(ModalDialogInfo info)
        {
            if (info == null || info.Handle == IntPtr.Zero) return false;
            try
            {
                if (!IsWindow(info.Handle)) return false;
                return PostMessage(info.Handle, WM_COMMAND, new IntPtr(IDCANCEL), IntPtr.Zero);
            }
            catch { return false; }
        }

        public static bool IsClosed(ModalDialogInfo info)
        {
            if (info == null || info.Handle == IntPtr.Zero) return false;
            try { return !IsWindow(info.Handle); }
            catch { return false; }
        }

        private const uint WM_COMMAND = 0x0111;
        private const int IDCANCEL = 2;

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private static string WindowText(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            try { GetWindowText(hwnd, sb, sb.Capacity); } catch { return null; }
            return sb.ToString();
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            try { GetClassName(hwnd, sb, sb.Capacity); } catch { return null; }
            string s = sb.ToString();
            return s.Length > 0 ? s : null;
        }

        /// <summary>
        /// GetWindowModuleFileName resolves the module associated with the window's
        /// class registration - which module CREATED this window, not which process
        /// owns the handle (that is always this same Revit process for anything
        /// ModalProbe can see; the useful distinction is Revit's own core vs. a
        /// third-party add-in's dialog on the same UI thread).
        /// </summary>
        private static string ModuleOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            int n;
            try { n = GetWindowModuleFileName(hwnd, sb, sb.Capacity); }
            catch { return null; }
            return n > 0 ? sb.ToString() : null;
        }

        private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);
        private delegate bool EnumChildWndProc(IntPtr hwnd, IntPtr lParam);

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

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildWndProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowModuleFileName(IntPtr hwnd, StringBuilder text, int maxCount);
    }
}
