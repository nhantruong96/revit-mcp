using System;
using System.Collections.Generic;
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
    /// It only acts while a request is in flight. With the server running but idle, the
    /// dialogs the user triggers by hand are left alone.
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
            }
        }

        public static void Detach()
        {
            lock (_sync)
            {
                if (_uiApp == null) return;
                _uiApp.DialogBoxShowing -= OnDialogBoxShowing;
                _uiApp = null;
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

        private static void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            if (Volatile.Read(ref _inFlight) == 0) return;

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

            string record = used != 0
                ? $"dismissed with {ButtonName(used)} - {detail}"
                : $"NOT dismissed, no result code accepted - {detail}";

            lock (_sync)
            {
                _dismissed.Add(record);
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
                    // Nothing in flight: drop the shared record so it cannot grow forever.
                    lock (_sync)
                    {
                        _dismissed.Clear();
                    }
                }
            }
        }
    }
}
