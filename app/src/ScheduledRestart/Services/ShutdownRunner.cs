using System;
using System.Diagnostics;
using System.Globalization;
using ScheduledRestart.Core;

namespace ScheduledRestart.Services
{
    /// <summary>Runs shutdown.exe - the only external process this app starts.</summary>
    internal static class ShutdownRunner
    {
        public static void ScheduleRestart(int seconds)
        {
            Run(string.Format(CultureInfo.InvariantCulture, "/r /f /t {0}", seconds));
        }

        public static void Abort()
        {
            Run("/a");
        }

        private static void Run(string arguments)
        {
            var info = new ProcessStartInfo(AppConstants.ShutdownExe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(info))
            {
                if (!p.WaitForExit(15000)) throw new TimeoutException("shutdown.exe לא הגיב.");
                if (p.ExitCode != 0)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "shutdown.exe החזיר קוד שגיאה {0}.", p.ExitCode));
            }
        }
    }
}
