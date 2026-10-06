using System;
using System.Globalization;
using System.Linq;

namespace ScheduledRestart.Core
{
    /// <summary>All user-facing Hebrew text that depends on schedule data.</summary>
    public static class HebrewText
    {
        private static readonly string[] DayNames = { "ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי", "שבת" };

        public static string DayName(DayOfWeek day)
        {
            return DayNames[(int)day];
        }

        public static string Time(TimeSpan time)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", time.Hours, time.Minutes);
        }

        public static string Date(DateTime date)
        {
            return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        /// <summary>"יום שישי, 09/10/2026 בשעה 03:00"</summary>
        public static string DateTimeText(DateTime value)
        {
            return string.Format("יום {0}, {1} בשעה {2}", DayName(value.DayOfWeek), Date(value), Time(value.TimeOfDay));
        }

        /// <summary>"ראשון, שלישי ושישי"</summary>
        public static string JoinList(string[] items)
        {
            if (items.Length == 0) return string.Empty;
            if (items.Length == 1) return items[0];
            return string.Join(", ", items.Take(items.Length - 1)) + " ו" + items[items.Length - 1];
        }

        /// <summary>Plain-Hebrew description, e.g. "כל שבוע ביום שישי בשעה 03:00".</summary>
        public static string Describe(RestartSchedule s)
        {
            string at = "בשעה " + Time(s.Time);
            switch (s.Kind)
            {
                case ScheduleKind.Once:
                    return string.Format("חד־פעמי ביום {0}, {1} {2}", DayName(s.Date.DayOfWeek), Date(s.Date), at);
                case ScheduleKind.Daily:
                    return "כל יום " + at;
                case ScheduleKind.Weekly:
                    if (s.Days.Length == 1)
                        return string.Format("כל שבוע ביום {0} {1}", DayName(s.Days[0]), at);
                    return string.Format("כל שבוע בימים {0} {1}", JoinList(s.Days.Select(DayName).ToArray()), at);
                case ScheduleKind.MonthlyDay:
                    return string.Format("כל חודש ב־{0} לחודש {1}", s.DayOfMonth, at);
                case ScheduleKind.MonthlyLastDay:
                    return "כל חודש ביום האחרון בחודש " + at;
                case ScheduleKind.MonthlyLastWeekday:
                    return string.Format("כל חודש ביום {0} האחרון בחודש {1}", DayName(s.Weekday), at);
                default:
                    return string.Empty;
            }
        }

        public static string NextRunLine(DateTime? next)
        {
            return next.HasValue
                ? "ההפעלה מחדש הבאה: " + DateTimeText(next.Value)
                : "ההפעלה מחדש הבאה: אין מועד עתידי";
        }

        /// <summary>Dropdown text for a warning option.</summary>
        public static string WarningOption(int minutes)
        {
            if (minutes == 0) return "ללא";
            if (minutes == 1) return "דקה";
            return minutes.ToString(CultureInfo.InvariantCulture) + " דקות";
        }

        /// <summary>Message shown to the user by shutdown.exe /c.</summary>
        public static string WarningMessage(int minutes)
        {
            string span = minutes == 1 ? "דקה" : minutes.ToString(CultureInfo.InvariantCulture) + " דקות";
            return string.Format("המחשב יופעל מחדש בעוד {0} לצורך תחזוקה. נא לשמור את העבודה.", span);
        }

        public static string WarningSummary(int minutes)
        {
            return minutes == 0
                ? "ללא התראה למשתמש"
                : string.Format("התראה למשתמש {0} לפני ההפעלה מחדש", minutes == 1 ? "דקה" : minutes.ToString(CultureInfo.InvariantCulture) + " דקות");
        }

        /// <summary>Text for Task Scheduler's last result code.</summary>
        public static string TaskResult(int code)
        {
            switch (code)
            {
                case 0: return "הצליחה";
                case 0x41301: return "פועלת כעת";
                case 0x41306: return "הופסקה";
                case 0x45B: return "המחשב בתהליך כיבוי";
                default: return string.Format(CultureInfo.InvariantCulture, "קוד 0x{0:X8}", code);
            }
        }
    }
}
