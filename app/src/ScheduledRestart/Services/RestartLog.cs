using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace ScheduledRestart.Services
{
    /// <summary>
    /// Appends to the log file shared with Set-ScheduledRestart.ps1 (UTF-8 with BOM) and to the
    /// Application event log. Logging never throws.
    /// </summary>
    internal sealed class RestartLog
    {
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private readonly string _path;
        private readonly string _eventSource;
        private bool? _eventSourceReady;

        /// <param name="path">Log file path.</param>
        /// <param name="eventSource">Event log source, or null to skip the event log.</param>
        public RestartLog(string path, string eventSource)
        {
            _path = path;
            _eventSource = eventSource;
        }

        public string Path
        {
            get { return _path; }
        }

        public static string CurrentUser
        {
            get
            {
                try { return WindowsIdentity.GetCurrent().Name; }
                catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
            }
        }

        public void Write(string message, EventLogEntryType type = EventLogEntryType.Information, int eventId = 0)
        {
            AppendLine(message);
            if (eventId != 0) WriteEvent(message, type, eventId);
        }

        /// <summary>All log lines, newest first.</summary>
        public string[] ReadNewestFirst()
        {
            if (!File.Exists(_path)) return new string[0];
            var lines = new List<string>();
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > 0) lines.Add(line);
                }
            }
            lines.Reverse();
            return lines.ToArray();
        }

        private void AppendLine(string message)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} | {1} | {2}{3}",
                    DateTime.Now, CurrentUser, message.Replace("\r", " ").Replace("\n", " "), Environment.NewLine);
                File.AppendAllText(_path, line, Utf8Bom);
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Log write failed: " + ex.Message);
            }
        }

        private void WriteEvent(string message, EventLogEntryType type, int eventId)
        {
            if (_eventSource == null) return;
            try
            {
                if (!_eventSourceReady.HasValue)
                {
                    if (!EventLog.SourceExists(_eventSource))
                        EventLog.CreateEventSource(_eventSource, Core.AppConstants.EventLogName);
                    _eventSourceReady = true;
                }
                if (_eventSourceReady.Value)
                    EventLog.WriteEntry(_eventSource, message + " (" + CurrentUser + ")", type, eventId);
            }
            catch (Exception ex)
            {
                _eventSourceReady = false;
                AppendLine("לא ניתן לכתוב ליומן האירועים של Windows: " + ex.Message);
            }
        }
    }
}
