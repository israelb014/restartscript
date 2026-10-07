using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ScheduledRestart.Core
{
    /// <summary>What the app reads back from a registered task's XML.</summary>
    public sealed class TaskXmlInfo
    {
        /// <summary>The schedule, or null when the task cannot be represented by the app.</summary>
        public RestartSchedule Schedule { get; internal set; }
        public bool Enabled { get; internal set; }
        public string UserId { get; internal set; }
        public string RunLevel { get; internal set; }
        public string Command { get; internal set; }
        public string Arguments { get; internal set; }
        public bool WakeToRun { get; internal set; }
        public DateTime? RegistrationDate { get; internal set; }
        public DateTime? StartBoundary { get; internal set; }
        public bool StartWhenAvailable { get; internal set; }

        public bool IsRecognized
        {
            get { return Schedule != null; }
        }
    }

    /// <summary>Builds and parses Task Scheduler XML (schema 1.2) for the restart task.</summary>
    public static class TaskXml
    {
        public const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        private static readonly string[] MonthNames =
        {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

        private static readonly int[] ValidWarningSeconds = RestartSchedule.WarningOptions.Select(m => m * 60).ToArray();

        /// <summary>shutdown.exe arguments for a schedule's warning setting.</summary>
        public static string BuildShutdownArguments(int warningMinutes)
        {
            if (warningMinutes == 0) return "/r /f /t 0";
            return string.Format(CultureInfo.InvariantCulture, "/r /f /t {0} /c \"{1}\"", warningMinutes * 60, HebrewText.WarningMessage(warningMinutes));
        }

        /// <summary>
        /// Reads the warning minutes from shutdown.exe arguments ("/t N"). Accepts the script's
        /// "/r /f /t 0 /d p:4:1 /c ..." form. Returns false when the arguments are not a restart
        /// with a supported delay.
        /// </summary>
        public static bool TryParseWarningMinutes(string arguments, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(arguments)) return false;
            if (!Regex.IsMatch(arguments, @"(^|\s)[/-]r(\s|$)", RegexOptions.IgnoreCase)) return false;
            Match t = Regex.Match(arguments, @"(^|\s)[/-]t\s+(\d+)(\s|$)", RegexOptions.IgnoreCase);
            int seconds = t.Success ? int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (!ValidWarningSeconds.Contains(seconds)) return false;
            minutes = seconds / 60;
            return true;
        }

        /// <summary>Builds the full task definition XML for a schedule.</summary>
        public static string Build(RestartSchedule schedule, string author, string description, DateTime today)
        {
            return Build(schedule, author, description, today, DateTime.Now);
        }

        /// <param name="registered">Written to RegistrationInfo/Date; used to judge missed runs.</param>
        public static string Build(RestartSchedule schedule, string author, string description, DateTime today, DateTime registered)
        {
            var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false };
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (XmlWriter w = XmlWriter.Create(sw, settings))
            {
                w.WriteStartDocument();
                w.WriteStartElement("Task", Namespace);
                w.WriteAttributeString("version", "1.2");

                w.WriteStartElement("RegistrationInfo", Namespace);
                w.WriteElementString("Date", Namespace, registered.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
                w.WriteElementString("Author", Namespace, author);
                w.WriteElementString("Description", Namespace, description);
                w.WriteEndElement();

                w.WriteStartElement("Triggers", Namespace);
                WriteTrigger(w, schedule, today);
                w.WriteEndElement();

                w.WriteStartElement("Principals", Namespace);
                w.WriteStartElement("Principal", Namespace);
                w.WriteAttributeString("id", "Author");
                w.WriteElementString("UserId", Namespace, AppConstants.SystemSid);
                w.WriteElementString("RunLevel", Namespace, "HighestAvailable");
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("Settings", Namespace);
                w.WriteElementString("MultipleInstancesPolicy", Namespace, "IgnoreNew");
                w.WriteElementString("DisallowStartIfOnBatteries", Namespace, "false");
                w.WriteElementString("StopIfGoingOnBatteries", Namespace, "false");
                w.WriteElementString("StartWhenAvailable", Namespace, "false");
                w.WriteElementString("AllowStartOnDemand", Namespace, "true");
                w.WriteElementString("Enabled", Namespace, "true");
                w.WriteElementString("Hidden", Namespace, "false");
                // A restart never fires late or wakes the machine: missed runs are skipped.
                w.WriteElementString("WakeToRun", Namespace, "false");
                w.WriteElementString("ExecutionTimeLimit", Namespace, "PT10M");
                w.WriteEndElement();

                w.WriteStartElement("Actions", Namespace);
                w.WriteAttributeString("Context", "Author");
                w.WriteStartElement("Exec", Namespace);
                w.WriteElementString("Command", Namespace, AppConstants.ShutdownExe);
                w.WriteElementString("Arguments", Namespace, BuildShutdownArguments(schedule.WarningMinutes));
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteEndElement();
                w.WriteEndDocument();
            }
            return sb.ToString();
        }

        private static void WriteTrigger(XmlWriter w, RestartSchedule s, DateTime today)
        {
            DateTime startDate = s.Kind == ScheduleKind.Once ? s.Date : today.Date;
            string boundary = (startDate + s.Time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

            if (s.Kind == ScheduleKind.Once)
            {
                w.WriteStartElement("TimeTrigger", Namespace);
                w.WriteElementString("StartBoundary", Namespace, boundary);
                w.WriteElementString("Enabled", Namespace, "true");
                w.WriteEndElement();
                return;
            }

            w.WriteStartElement("CalendarTrigger", Namespace);
            w.WriteElementString("StartBoundary", Namespace, boundary);
            w.WriteElementString("Enabled", Namespace, "true");
            switch (s.Kind)
            {
                case ScheduleKind.Daily:
                    w.WriteStartElement("ScheduleByDay", Namespace);
                    w.WriteElementString("DaysInterval", Namespace, "1");
                    w.WriteEndElement();
                    break;
                case ScheduleKind.Weekly:
                    w.WriteStartElement("ScheduleByWeek", Namespace);
                    w.WriteStartElement("DaysOfWeek", Namespace);
                    foreach (DayOfWeek d in s.Days) w.WriteElementString(d.ToString(), Namespace, null);
                    w.WriteEndElement();
                    w.WriteElementString("WeeksInterval", Namespace, "1");
                    w.WriteEndElement();
                    break;
                case ScheduleKind.MonthlyDay:
                case ScheduleKind.MonthlyLastDay:
                    w.WriteStartElement("ScheduleByMonth", Namespace);
                    w.WriteStartElement("DaysOfMonth", Namespace);
                    w.WriteElementString("Day", Namespace,
                        s.Kind == ScheduleKind.MonthlyLastDay ? "Last" : s.DayOfMonth.ToString(CultureInfo.InvariantCulture));
                    w.WriteEndElement();
                    WriteMonths(w);
                    w.WriteEndElement();
                    break;
                case ScheduleKind.MonthlyLastWeekday:
                    w.WriteStartElement("ScheduleByMonthDayOfWeek", Namespace);
                    w.WriteStartElement("Weeks", Namespace);
                    w.WriteElementString("Week", Namespace, "Last");
                    w.WriteEndElement();
                    w.WriteStartElement("DaysOfWeek", Namespace);
                    w.WriteElementString(s.Weekday.ToString(), Namespace, null);
                    w.WriteEndElement();
                    WriteMonths(w);
                    w.WriteEndElement();
                    break;
            }
            w.WriteEndElement();
        }

        private static void WriteMonths(XmlWriter w)
        {
            w.WriteStartElement("Months", Namespace);
            foreach (string m in MonthNames) w.WriteElementString(m, Namespace, null);
            w.WriteEndElement();
        }

        /// <summary>Parses task XML written by this app, by Set-ScheduledRestart.ps1 or by Task Scheduler itself.</summary>
        public static TaskXmlInfo Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", Namespace);

            var info = new TaskXmlInfo
            {
                Enabled = ReadBool(doc.SelectSingleNode("/t:Task/t:Settings/t:Enabled", ns), true),
                UserId = Text(doc.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:UserId", ns)),
                RunLevel = Text(doc.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:RunLevel", ns)),
                WakeToRun = ReadBool(doc.SelectSingleNode("/t:Task/t:Settings/t:WakeToRun", ns), false),
                StartWhenAvailable = ReadBool(doc.SelectSingleNode("/t:Task/t:Settings/t:StartWhenAvailable", ns), false)
            };
            DateTime parsed;
            if (TryParseBoundary(Text(doc.SelectSingleNode("/t:Task/t:RegistrationInfo/t:Date", ns)), out parsed)) info.RegistrationDate = parsed;
            if (TryParseBoundary(Text(doc.SelectSingleNode("/t:Task/t:Triggers/*/t:StartBoundary", ns)), out parsed)) info.StartBoundary = parsed;

            XmlNodeList execs = doc.SelectNodes("/t:Task/t:Actions/t:Exec", ns);
            if (execs.Count == 1)
            {
                info.Command = Text(execs[0].SelectSingleNode("t:Command", ns));
                info.Arguments = Text(execs[0].SelectSingleNode("t:Arguments", ns));
            }

            int warning;
            bool actionOk = execs.Count == 1
                && doc.SelectNodes("/t:Task/t:Actions/*", ns).Count == 1
                && info.Command != null
                && info.Command.Trim('"').EndsWith("shutdown.exe", StringComparison.OrdinalIgnoreCase)
                && TryParseWarningMinutes(info.Arguments, out warning);
            if (!actionOk) return info;
            TryParseWarningMinutes(info.Arguments, out warning);

            XmlNode triggers = doc.SelectSingleNode("/t:Task/t:Triggers", ns);
            XmlElement[] list = triggers == null ? new XmlElement[0] : triggers.ChildNodes.OfType<XmlElement>().ToArray();
            if (list.Length != 1) return info;
            info.Schedule = ParseTrigger(list[0], ns, warning);
            return info;
        }

        /// <summary>The single trigger as an app schedule (ignoring actions), or null. Never throws.</summary>
        public static RestartSchedule TryParseSchedule(string xml)
        {
            try
            {
                XmlNamespaceManager ns;
                XmlElement[] triggers = ReadTriggers(xml, out ns);
                return triggers.Length == 1 ? ParseTrigger(triggers[0], ns, 0) : null;
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException || ex is FormatException || ex is System.Xml.XPath.XPathException)
            {
                return null;
            }
        }

        /// <summary>Hebrew description of a task's triggers, for any task. Never throws.</summary>
        public static string DescribeTriggers(string xml)
        {
            try
            {
                XmlNamespaceManager ns;
                XmlElement[] triggers = ReadTriggers(xml, out ns);
                if (triggers.Length == 0) return "ללא טריגר";
                if (triggers.Length > 1) return "מספר טריגרים";
                RestartSchedule schedule = ParseTrigger(triggers[0], ns, 0);
                if (schedule != null) return HebrewText.Describe(schedule);
                switch (triggers[0].LocalName)
                {
                    case "BootTrigger": return "בכל הפעלת מחשב";
                    case "LogonTrigger": return "בכניסת משתמש";
                    case "IdleTrigger": return "כשהמחשב במצב סרק";
                    case "EventTrigger": return "בעת אירוע ביומן האירועים";
                    case "RegistrationTrigger": return "בעת יצירת המשימה או עדכונה";
                    case "SessionStateChangeTrigger": return "בשינוי מצב התחברות";
                    case "TimeTrigger": return "חד־פעמי עם חזרה";
                    case "CalendarTrigger": return "תזמון לוח שנה מותאם";
                    default: return "טריגר לא מוכר";
                }
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException || ex is FormatException || ex is System.Xml.XPath.XPathException)
            {
                return "לא ניתן לקרוא את הטריגר";
            }
        }

        private static XmlElement[] ReadTriggers(string xml, out XmlNamespaceManager ns)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", Namespace);
            XmlNode triggers = doc.SelectSingleNode("/t:Task/t:Triggers", ns);
            return triggers == null ? new XmlElement[0] : triggers.ChildNodes.OfType<XmlElement>().ToArray();
        }

        private static RestartSchedule ParseTrigger(XmlElement trigger, XmlNamespaceManager ns, int warning)
        {
            if (trigger.NamespaceURI != Namespace) return null;
            DateTime start;
            if (!TryParseBoundary(Text(trigger.SelectSingleNode("t:StartBoundary", ns)), out start)) return null;
            TimeSpan time = new TimeSpan(start.Hour, start.Minute, 0);
            if (start.Second != 0) return null;

            if (trigger.LocalName == "TimeTrigger")
            {
                if (trigger.SelectSingleNode("t:Repetition", ns) != null) return null;
                return RestartSchedule.Once(start.Date, time, warning);
            }
            if (trigger.LocalName != "CalendarTrigger" || trigger.SelectSingleNode("t:Repetition", ns) != null) return null;

            XmlNode byDay = trigger.SelectSingleNode("t:ScheduleByDay", ns);
            if (byDay != null)
            {
                return IntervalIsOne(byDay.SelectSingleNode("t:DaysInterval", ns)) ? RestartSchedule.Daily(time, warning) : null;
            }

            XmlNode byWeek = trigger.SelectSingleNode("t:ScheduleByWeek", ns);
            if (byWeek != null)
            {
                if (!IntervalIsOne(byWeek.SelectSingleNode("t:WeeksInterval", ns))) return null;
                DayOfWeek[] days = ParseDays(byWeek.SelectSingleNode("t:DaysOfWeek", ns));
                return days != null && days.Length > 0 ? RestartSchedule.Weekly(days, time, warning) : null;
            }

            XmlNode byMonth = trigger.SelectSingleNode("t:ScheduleByMonth", ns);
            if (byMonth != null)
            {
                if (!AllMonths(byMonth.SelectSingleNode("t:Months", ns))) return null;
                XmlNodeList dayNodes = byMonth.SelectNodes("t:DaysOfMonth/t:Day", ns);
                if (dayNodes.Count != 1) return null;
                string day = Text(dayNodes[0]);
                if (string.Equals(day, "Last", StringComparison.OrdinalIgnoreCase)) return RestartSchedule.MonthlyLastDay(time, warning);
                int n;
                return int.TryParse(day, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 31
                    ? RestartSchedule.MonthlyDay(n, time, warning)
                    : null;
            }

            XmlNode byDow = trigger.SelectSingleNode("t:ScheduleByMonthDayOfWeek", ns);
            if (byDow != null)
            {
                if (!AllMonths(byDow.SelectSingleNode("t:Months", ns))) return null;
                XmlNodeList weeks = byDow.SelectNodes("t:Weeks/t:Week", ns);
                if (weeks.Count != 1 || !string.Equals(Text(weeks[0]), "Last", StringComparison.OrdinalIgnoreCase)) return null;
                DayOfWeek[] days = ParseDays(byDow.SelectSingleNode("t:DaysOfWeek", ns));
                return days != null && days.Length == 1 ? RestartSchedule.MonthlyLastWeekday(days[0], time, warning) : null;
            }
            return null;
        }

        private static bool TryParseBoundary(string text, out DateTime value)
        {
            value = DateTime.MinValue;
            if (text == null || text.Length < 19) return false;
            return DateTime.TryParseExact(text.Substring(0, 19), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        private static DayOfWeek[] ParseDays(XmlNode daysOfWeek)
        {
            if (daysOfWeek == null) return null;
            var result = new System.Collections.Generic.List<DayOfWeek>();
            foreach (XmlElement e in daysOfWeek.ChildNodes.OfType<XmlElement>())
            {
                DayOfWeek d;
                if (!Enum.TryParse(e.LocalName, false, out d) || !Enum.IsDefined(typeof(DayOfWeek), d)) return null;
                result.Add(d);
            }
            return result.ToArray();
        }

        private static bool AllMonths(XmlNode months)
        {
            if (months == null) return false;
            string[] names = months.ChildNodes.OfType<XmlElement>().Select(e => e.LocalName).ToArray();
            return MonthNames.All(names.Contains) && names.Length == MonthNames.Length;
        }

        private static bool IntervalIsOne(XmlNode node)
        {
            return node == null || Text(node) == "1";
        }

        private static bool ReadBool(XmlNode node, bool defaultValue)
        {
            string text = Text(node);
            if (text == null) return defaultValue;
            return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string Text(XmlNode node)
        {
            return node == null ? null : node.InnerText.Trim();
        }
    }
}
