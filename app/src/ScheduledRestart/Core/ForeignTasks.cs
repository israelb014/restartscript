using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace ScheduledRestart.Core
{
    /// <summary>What can be learned about any task from its XML alone.</summary>
    public sealed class ForeignTaskAnalysis
    {
        public PowerAction Action { get; internal set; }
        public bool DetectedInScript { get; internal set; }
        public string CommandLine { get; internal set; }
        public string UserId { get; internal set; }
        public string ScheduleText { get; internal set; }

        /// <summary>The trigger as an app schedule when it fits the model (adoptable), otherwise null.</summary>
        public RestartSchedule Schedule { get; internal set; }

        public bool CanAdopt
        {
            get { return Action == PowerAction.Restart && Schedule != null; }
        }
    }

    public static class ForeignTaskAnalyzer
    {
        /// <summary>
        /// Analyzes a task definition. Returns null when the XML cannot be parsed. Never throws.
        /// <paramref name="readScript"/> returns a script file's text (or null) for .bat/.cmd/.ps1 targets.
        /// </summary>
        public static ForeignTaskAnalysis Analyze(string xml, Func<string, string, string> readScript)
        {
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xml);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", TaskXml.Namespace);

                var result = new ForeignTaskAnalysis { Action = PowerAction.None };
                var lines = new List<string>();
                foreach (XmlNode exec in doc.SelectNodes("/t:Task/t:Actions/t:Exec", ns))
                {
                    string command = Text(exec.SelectSingleNode("t:Command", ns)) ?? string.Empty;
                    string arguments = Text(exec.SelectSingleNode("t:Arguments", ns)) ?? string.Empty;
                    string workingDirectory = Text(exec.SelectSingleNode("t:WorkingDirectory", ns));
                    lines.Add((command + " " + arguments).Trim());
                    Classification c = CommandClassifier.Classify(command, arguments, workingDirectory, readScript);
                    if ((int)c.Action > (int)result.Action)
                    {
                        result.Action = c.Action;
                        result.DetectedInScript = c.DetectedInScript;
                    }
                }
                result.CommandLine = string.Join(Environment.NewLine, lines);
                XmlNode principal = doc.SelectSingleNode("/t:Task/t:Principals/t:Principal", ns);
                if (principal != null)
                    result.UserId = Text(principal.SelectSingleNode("t:UserId", ns)) ?? Text(principal.SelectSingleNode("t:GroupId", ns));
                result.ScheduleText = TaskXml.DescribeTriggers(xml);
                result.Schedule = TaskXml.TryParseSchedule(xml);
                return result;
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException || ex is FormatException
                                       || ex is System.Xml.XPath.XPathException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        private static string Text(XmlNode node)
        {
            return node == null ? null : node.InnerText.Trim();
        }
    }

    public enum OneTimeResult
    {
        Pending,
        Executed,
        Missed
    }

    /// <summary>Missed-run and one-time outcome rules.</summary>
    public static class RunHistory
    {
        public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

        /// <summary>
        /// True when the latest expected occurrence (after registration, more than 10 minutes ago) has no
        /// matching last run - the machine was off or asleep.
        /// </summary>
        public static bool IsMissed(RestartSchedule schedule, DateTime? registered, DateTime? lastRun, DateTime now)
        {
            if (schedule == null || schedule.Kind == ScheduleKind.Once) return false;
            DateTime? expected = schedule.GetPreviousRun(now - Grace);
            if (!expected.HasValue) return false;
            if (registered.HasValue && expected.Value <= registered.Value) return false;
            return !(lastRun.HasValue && lastRun.Value >= expected.Value.AddMinutes(-1));
        }

        public static OneTimeResult EvaluateOneTime(RestartSchedule schedule, DateTime? lastRun, DateTime now)
        {
            if (schedule == null || schedule.Kind != ScheduleKind.Once) return OneTimeResult.Pending;
            DateTime at = schedule.Date + schedule.Time;
            if (now < at + Grace) return OneTimeResult.Pending;
            return lastRun.HasValue && lastRun.Value >= at.AddMinutes(-1) ? OneTimeResult.Executed : OneTimeResult.Missed;
        }

        public static string OneTimeText(OneTimeResult result, DateTime? lastRun)
        {
            if (result == OneTimeResult.Executed && lastRun.HasValue)
                return string.Format("התזמון החד־פעמי בוצע ב־{0} בשעה {1}", HebrewText.Date(lastRun.Value), HebrewText.Time(lastRun.Value.TimeOfDay));
            if (result == OneTimeResult.Missed)
                return "התזמון החד־פעמי לא בוצע, המחשב היה כבוי";
            return null;
        }
    }

    /// <summary>Keeps the shared log file small: at the size limit it becomes .old and a new file starts.</summary>
    public static class LogRotation
    {
        public const long MaxBytes = 1024 * 1024;

        public static string OldPath(string logPath)
        {
            return Path.ChangeExtension(logPath, ".old");
        }

        /// <returns>True when the log was rotated.</returns>
        public static bool RotateIfNeeded(string logPath, long maxBytes = MaxBytes)
        {
            try
            {
                var file = new FileInfo(logPath);
                if (!file.Exists || file.Length < maxBytes) return false;
                string old = OldPath(logPath);
                if (File.Exists(old)) File.Delete(old);
                File.Move(logPath, old);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Licensee embedded at build time as assembly metadata (LicenseeName, LicenseId).</summary>
    public sealed class LicenseInfo
    {
        public const string NameKey = "LicenseeName";
        public const string IdKey = "LicenseId";

        public LicenseInfo(string name, string id)
        {
            Name = (name ?? string.Empty).Trim();
            Id = (id ?? string.Empty).Trim();
        }

        public string Name { get; private set; }
        public string Id { get; private set; }

        public bool IsLicensed
        {
            get { return Name.Length > 0; }
        }

        public string DisplayLine
        {
            get { return IsLicensed ? "מורשה ל: " + Name : "עותק הדגמה, לא מורשה להפצה"; }
        }

        public static LicenseInfo FromMetadata(IEnumerable<KeyValuePair<string, string>> metadata)
        {
            string name = null, id = null;
            foreach (KeyValuePair<string, string> pair in metadata)
            {
                if (pair.Key == NameKey) name = pair.Value;
                else if (pair.Key == IdKey) id = pair.Value;
            }
            return new LicenseInfo(name, id);
        }
    }

    public static class BackupNames
    {
        private const string Invalid = "\\/:*?\"<>|";

        /// <summary>"&lt;safe-name&gt;-yyyyMMdd-HHmmss.xml" for a backup of a deleted task.</summary>
        public static string ForTask(string taskName, DateTime when)
        {
            var sb = new StringBuilder();
            foreach (char c in (taskName ?? string.Empty).Trim())
                sb.Append(c < 32 || Invalid.IndexOf(c) >= 0 ? '_' : c);
            string safe = sb.ToString().Trim(' ', '.');
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            if (safe.Length == 0) safe = "task";
            return safe + "-" + when.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".xml";
        }
    }
}
