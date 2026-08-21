using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMCPSDK.API.Interfaces;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Dismisses the modal dialogs Revit raises while an MCP command is running, and keeps
    /// a record of each one so the command's response can say what was clicked away.
    ///
    /// A modal dialog blocks the external event with nobody there to click it: the command
    /// times out, the AI client retries, and the retry repeats whatever the first run had
    /// already done. The per-transaction failure preprocessor in the command set cannot
    /// reach dialogs raised outside a transaction - TaskDialog.Show inside a handler,
    /// "Duplicate Types" during CopyElements - so this guard sits above it, on the
    /// application's DialogBoxShowing event.
    ///
    /// Two things the managed event alone does not cover, and this class does:
    ///
    /// 1. Work that outlives the request. The command times out after 60 s but Revit keeps
    ///    running the paste, and the dialog appears afterwards. Disarming when the request
    ///    returns would leave that dialog on screen forever. So the guard stays armed until
    ///    Revit next reports Idling - which it cannot do while an operation runs or a modal
    ///    dialog is up. That is exactly the window that needs covering, and no wider.
    ///
    /// 2. Dialogs DialogBoxShowing cannot answer. Some Revit dialogs reject every standard
    ///    result code ("Error - cannot be ignored" offers only a resolution button such as
    ///    "Disconnect", and its OK button is disabled). For those a background watchdog finds
    ///    the modal window through Win32 and clicks the button by name.
    ///
    /// Both only act while a request is in flight or Revit has not gone idle since one. With
    /// the server running but idle, dialogs the user opens by hand are left alone.
    /// </summary>
    public static class DialogGuard
    {
        // WinUser.h button ids and MessageBox type bits. Revit's OverrideResult takes
        // these for message boxes and task dialogs alike.
        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDYES = 6;
        private const int MB_TYPEMASK = 0x0F;
        private const int MB_YESNOCANCEL = 0x03;
        private const int MB_YESNO = 0x04;

        private static readonly object _sync = new object();
        private static readonly List<string> _dismissed = new List<string>();
        private static UIApplication _uiApp;
        private static ILogger _logger;
        private static int _inFlight;

        // Set when the last request returns, cleared on the first Idling after that. While it
        // is set the guard stays armed, because Revit only idles once it has finished what the
        // request started and no modal dialog is blocking it.
        private static bool _awaitingIdle;

        private static Thread _watchdog;
        private static volatile bool _watchdogRun;

        /// <summary>True while dialogs Revit raises should be answered automatically.</summary>
        private static bool Armed
        {
            get
            {
                if (Volatile.Read(ref _inFlight) > 0) return true;
                lock (_sync) return _awaitingIdle;
            }
        }

        /// <summary>
        /// Subscribe to the application's dialog event. Call on Revit's UI thread - the
        /// ribbon button that starts the server qualifies.
        /// </summary>
        public static void Attach(UIApplication uiApp, ILogger logger)
        {
            lock (_sync)
            {
                if (_uiApp != null || uiApp == null) return;
                _uiApp = uiApp;
                _logger = logger;
                _uiApp.DialogBoxShowing += OnDialogBoxShowing;
                _uiApp.Idling += OnIdling;
            }

            StartWatchdog();
        }

        public static void Detach()
        {
            StopWatchdog();

            lock (_sync)
            {
                if (_uiApp == null) return;
                _uiApp.DialogBoxShowing -= OnDialogBoxShowing;
                _uiApp.Idling -= OnIdling;
                _uiApp = null;
                _awaitingIdle = false;
                _dismissed.Clear();
            }
        }

        /// <summary>
        /// Mark a request as in flight. Dispose the scope when the command returns; read
        /// <see cref="RequestScope.Dismissed"/> before that to learn which dialogs were
        /// closed meanwhile.
        /// </summary>
        public static RequestScope BeginRequest()
        {
            return new RequestScope();
        }

        // Revit raises Idling only when it is genuinely idle: no operation running, no modal
        // dialog on screen. That makes it the right signal for "whatever the last request set
        // in motion has finished" - including work that outlived a client-side timeout.
        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (Volatile.Read(ref _inFlight) > 0) return;

            lock (_sync)
            {
                if (!_awaitingIdle) return;
                _awaitingIdle = false;
                _dismissed.Clear();
            }
        }

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            if (!Armed) return;

            string detail = Describe(e);
            int preferred = PreferredResult(e);
            int[] candidates = preferred == IDOK
                ? new[] { IDOK, IDCANCEL }
                : new[] { preferred, IDOK, IDCANCEL };

            int used = 0;
            foreach (int code in candidates)
            {
                if (TryOverride(e, code))
                {
                    used = code;
                    break;
                }
            }

            // No result code accepted: the window will actually appear, and the Win32
            // watchdog gets its turn at it. Say so, rather than reporting a dead end.
            string record = used != 0
                ? $"dismissed with {ButtonName(used)} - {detail}"
                : $"no result code accepted, left to the click watchdog - {detail}";

            Record(record);
        }

        private static void Record(string record)
        {
            lock (_sync)
            {
                _dismissed.Add(record);

                // Nothing reads this list once no request is in flight, but a long paste can
                // raise many dialogs; keep it from growing without bound.
                if (_dismissed.Count > 200) _dismissed.RemoveRange(0, _dismissed.Count - 200);
            }

            if (_logger != null) _logger.Warning("Revit dialog during MCP command: {0}", record);
        }

        private static string Describe(DialogBoxShowingEventArgs e)
        {
            string message = null;
            if (e is TaskDialogShowingEventArgs task) message = task.Message;
            else if (e is MessageBoxShowingEventArgs box) message = box.Message;

            // DialogId is empty for plain TaskDialog.Show and for standard message boxes.
            string id = string.IsNullOrEmpty(e.DialogId) ? e.GetType().Name : e.DialogId;
            return string.IsNullOrEmpty(message) ? id : $"{id}: {message}";
        }

        private static int PreferredResult(DialogBoxShowingEventArgs e)
        {
            // A Yes/No message box has no OK button; "Yes" is the answer that lets the
            // operation the caller asked for carry on.
            if (e is MessageBoxShowingEventArgs box)
            {
                int type = box.DialogType & MB_TYPEMASK;
                if (type == MB_YESNO || type == MB_YESNOCANCEL) return IDYES;
            }
            return IDOK;
        }

        private static bool TryOverride(DialogBoxShowingEventArgs e, int code)
        {
            try
            {
                return e.OverrideResult(code);
            }
            catch
            {
                // A dialog that has no button for this code may throw instead of
                // returning false; either way, try the next code.
                return false;
            }
        }

        private static string ButtonName(int code)
        {
            switch (code)
            {
                case IDOK: return "OK";
                case IDYES: return "Yes";
                case IDCANCEL: return "Cancel";
                default: return code.ToString();
            }
        }

        public sealed class RequestScope : IDisposable
        {
            private readonly int _start;
            private bool _disposed;

            internal RequestScope()
            {
                lock (_sync)
                {
                    _start = _dismissed.Count;
                    _awaitingIdle = false;
                }
                Interlocked.Increment(ref _inFlight);
            }

            /// <summary>Dialogs dismissed since this scope began. Read before Dispose.</summary>
            public IReadOnlyList<string> Dismissed
            {
                get
                {
                    lock (_sync)
                    {
                        int start = Math.Min(_start, _dismissed.Count);
                        return _dismissed.GetRange(start, _dismissed.Count - start);
                    }
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (Interlocked.Decrement(ref _inFlight) == 0)
                {
                    // The request has returned, but Revit may still be running what it
                    // started - a timed-out CopyElements finishes on its own and raises its
                    // dialog afterwards. Stay armed until Revit idles.
                    lock (_sync)
                    {
                        _awaitingIdle = true;
                    }
                }
            }
        }

        // -----------------------------------------------------------------------------
        // Win32 click watchdog
        //
        // Last resort for dialogs OverrideResult cannot answer, and for anything that slips
        // past the managed event. It runs on its own thread because the UI thread is the one
        // the dialog is blocking - nothing on it could do this.
        // -----------------------------------------------------------------------------

        private const string DialogClass = "#32770";
        private const uint BM_CLICK = 0x00F5;

        // Order matters: an answer that lets the operation carry on comes before one that
        // abandons it. "Disconnect" resolves Revit's "the family no longer matches the
        // properties for the System" error, whose OK button is disabled.
        private static readonly string[] PreferredButtons =
        {
            "OK", "Yes", "Continue", "Disconnect", "Close", "Ignore"
        };

        // How long a dialog must have been up before the watchdog clicks it. The managed
        // OverrideResult path is faster and more precise; give it the first turn.
        private static readonly TimeSpan ClickDelay = TimeSpan.FromSeconds(2);

        private static void StartWatchdog()
        {
            if (_watchdog != null) return;

            _watchdogRun = true;
            _watchdog = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "MCP dialog watchdog"
            };
            _watchdog.Start();
        }

        private static void StopWatchdog()
        {
            _watchdogRun = false;
            Thread thread = _watchdog;
            _watchdog = null;
            if (thread != null && thread.IsAlive) thread.Join(2000);
        }

        private static void WatchdogLoop()
        {
            // Dialog handle -> when it was first seen, so a dialog is only clicked once the
            // managed path has had its chance at it.
            var seen = new Dictionary<IntPtr, DateTime>();

            while (_watchdogRun)
            {
                try
                {
                    if (Armed) SweepDialogs(seen);
                    else if (seen.Count > 0) seen.Clear();
                }
                catch
                {
                    // A watchdog that throws is worse than one that misses a dialog.
                }

                Thread.Sleep(500);
            }
        }

        private static void SweepDialogs(Dictionary<IntPtr, DateTime> seen)
        {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            var dialogs = new List<IntPtr>();

            EnumWindows((hwnd, unused) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                if (owner != pid) return true;
                if (ClassOf(hwnd) != DialogClass) return true;
                dialogs.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            DateTime now = DateTime.UtcNow;
            var live = new HashSet<IntPtr>(dialogs);
            var stale = new List<IntPtr>();
            foreach (IntPtr key in seen.Keys)
                if (!live.Contains(key)) stale.Add(key);
            foreach (IntPtr key in stale) seen.Remove(key);

            foreach (IntPtr dialog in dialogs)
            {
                string title = TextOf(dialog);

                // Revit's warning palette shares the dialog class but is modeless and blocks
                // nothing; clicking its navigation arrows would just be noise.
                if (title.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)) continue;

                DateTime firstSeen;
                if (!seen.TryGetValue(dialog, out firstSeen))
                {
                    seen[dialog] = now;
                    continue;
                }

                if (now - firstSeen < ClickDelay) continue;

                IntPtr button = FindPreferredButton(dialog);
                if (button == IntPtr.Zero) continue;

                string label = TextOf(button).Replace("&", string.Empty);
                SendMessage(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                seen.Remove(dialog);
                Record($"clicked [{label}] on modal dialog [{title}]");
            }
        }

        private static IntPtr FindPreferredButton(IntPtr dialog)
        {
            var buttons = new List<IntPtr>();
            EnumChildWindows(dialog, (child, unused) =>
            {
                if (ClassOf(child).IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    buttons.Add(child);
                return true;
            }, IntPtr.Zero);

            foreach (string wanted in PreferredButtons)
            {
                foreach (IntPtr button in buttons)
                {
                    if (!IsWindowEnabled(button)) continue;
                    if (!IsWindowVisible(button)) continue;
                    string label = TextOf(button).Replace("&", string.Empty).Trim();
                    if (string.Equals(label, wanted, StringComparison.OrdinalIgnoreCase))
                        return button;
                }
            }

            return IntPtr.Zero;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string TextOf(IntPtr hwnd)
        {
            var buffer = new StringBuilder(512);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
