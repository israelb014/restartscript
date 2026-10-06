using System;
using System.IO;

namespace ScheduledRestart.Core
{
    /// <summary>Names and paths shared with Set-ScheduledRestart.ps1. Keep them identical to the script.</summary>
    public static class AppConstants
    {
        public const string TaskFolderName = "ScheduledRestart";
        public const string TaskName = "ScheduledRestart";
        public const string LogDirectory = @"C:\ProgramData\ScheduledRestart";
        public const string LogFile = @"C:\ProgramData\ScheduledRestart\ScheduledRestart.log";
        public const string BackupFile = @"C:\ProgramData\ScheduledRestart\ScheduledRestart.previous.xml";
        public const string EventSource = "ScheduledRestart";
        public const string EventLogName = "Application";
        public const string SystemSid = "S-1-5-18";

        /// <summary>
        /// Maker's website, opened from the About window. Compile-time constant: never built from
        /// user input, files, the registry or the network.
        /// </summary>
        public const string WebsiteUrl = "https://ib-fix.com";

        public const int EventCreated = 1001;
        public const int EventDeleted = 1002;
        public const int EventPaused = 1003;
        public const int EventResumed = 1004;
        public const int EventRestartNow = 1005;
        public const int EventRestartCancelled = 1006;
        public const int EventFailure = 1007;
        public const int EventRollback = 1008;

        /// <summary>Full path of shutdown.exe used in the task action and for "restart now".</summary>
        public static string ShutdownExe
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "shutdown.exe"); }
        }
    }
}
