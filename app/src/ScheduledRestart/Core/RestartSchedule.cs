using System;
using System.Linq;

namespace ScheduledRestart.Core
{
    public enum ScheduleKind
    {
        Once,
        Daily,
        Weekly,
        MonthlyDay,
        MonthlyLastDay,
        MonthlyLastWeekday
    }

    /// <summary>A restart schedule as the app understands it. Immutable after construction.</summary>
    public sealed class RestartSchedule : IEquatable<RestartSchedule>
    {
        public static readonly int[] WarningOptions = { 0, 1, 5, 10, 15 };

        public ScheduleKind Kind { get; private set; }

        /// <summary>Date of a one-time restart (date part only). Unused for other kinds.</summary>
        public DateTime Date { get; private set; }

        /// <summary>Time of day, whole minutes.</summary>
        public TimeSpan Time { get; private set; }

        /// <summary>Weekdays for weekly schedules, sorted Sunday first.</summary>
        public DayOfWeek[] Days { get; private set; }

        /// <summary>Day of month (1-31) for MonthlyDay.</summary>
        public int DayOfMonth { get; private set; }

        /// <summary>Weekday for MonthlyLastWeekday.</summary>
        public DayOfWeek Weekday { get; private set; }

        /// <summary>Minutes between the user warning and the restart (0 = no warning).</summary>
        public int WarningMinutes { get; private set; }

        private RestartSchedule(ScheduleKind kind, TimeSpan time, int warningMinutes)
        {
            if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException("time");
            if (!WarningOptions.Contains(warningMinutes))
                throw new ArgumentOutOfRangeException("warningMinutes");
            Kind = kind;
            Time = new TimeSpan(time.Hours, time.Minutes, 0);
            WarningMinutes = warningMinutes;
            Days = new DayOfWeek[0];
            DayOfMonth = 1;
        }

        public static RestartSchedule Once(DateTime date, TimeSpan time, int warningMinutes = 0)
        {
            return new RestartSchedule(ScheduleKind.Once, time, warningMinutes) { Date = date.Date };
        }

        public static RestartSchedule Daily(TimeSpan time, int warningMinutes = 0)
        {
            return new RestartSchedule(ScheduleKind.Daily, time, warningMinutes);
        }

        public static RestartSchedule Weekly(DayOfWeek[] days, TimeSpan time, int warningMinutes = 0)
        {
            if (days == null || days.Length == 0) throw new ArgumentException("At least one day is required.", "days");
            return new RestartSchedule(ScheduleKind.Weekly, time, warningMinutes)
            {
                Days = days.Distinct().OrderBy(d => (int)d).ToArray()
            };
        }

        public static RestartSchedule MonthlyDay(int dayOfMonth, TimeSpan time, int warningMinutes = 0)
        {
            if (dayOfMonth < 1 || dayOfMonth > 31) throw new ArgumentOutOfRangeException("dayOfMonth");
            return new RestartSchedule(ScheduleKind.MonthlyDay, time, warningMinutes) { DayOfMonth = dayOfMonth };
        }

        public static RestartSchedule MonthlyLastDay(TimeSpan time, int warningMinutes = 0)
        {
            return new RestartSchedule(ScheduleKind.MonthlyLastDay, time, warningMinutes);
        }

        public static RestartSchedule MonthlyLastWeekday(DayOfWeek weekday, TimeSpan time, int warningMinutes = 0)
        {
            return new RestartSchedule(ScheduleKind.MonthlyLastWeekday, time, warningMinutes) { Weekday = weekday };
        }

        /// <summary>Next occurrence strictly after <paramref name="from"/>, or null when there is none.</summary>
        public DateTime? GetNextRun(DateTime from)
        {
            switch (Kind)
            {
                case ScheduleKind.Once:
                {
                    DateTime at = Date + Time;
                    return at > from ? at : (DateTime?)null;
                }
                case ScheduleKind.Daily:
                {
                    DateTime at = from.Date + Time;
                    return at > from ? at : at.AddDays(1);
                }
                case ScheduleKind.Weekly:
                    for (int i = 0; i <= 7; i++)
                    {
                        DateTime at = from.Date.AddDays(i) + Time;
                        if (at > from && Days.Contains(at.DayOfWeek)) return at;
                    }
                    return null;
                default:
                    DateTime month = new DateTime(from.Year, from.Month, 1);
                    for (int m = 0; m < 24; m++, month = month.AddMonths(1))
                    {
                        DateTime? day = GetMonthlyDate(month);
                        if (day.HasValue && day.Value + Time > from) return day.Value + Time;
                    }
                    return null;
            }
        }

        /// <summary>Latest occurrence at or before <paramref name="atOrBefore"/>, or null when there is none.</summary>
        public DateTime? GetPreviousRun(DateTime atOrBefore)
        {
            switch (Kind)
            {
                case ScheduleKind.Once:
                {
                    DateTime at = Date + Time;
                    return at <= atOrBefore ? at : (DateTime?)null;
                }
                case ScheduleKind.Daily:
                {
                    DateTime at = atOrBefore.Date + Time;
                    return at <= atOrBefore ? at : at.AddDays(-1);
                }
                case ScheduleKind.Weekly:
                    for (int i = 0; i <= 7; i++)
                    {
                        DateTime at = atOrBefore.Date.AddDays(-i) + Time;
                        if (at <= atOrBefore && Days.Contains(at.DayOfWeek)) return at;
                    }
                    return null;
                default:
                    DateTime month = new DateTime(atOrBefore.Year, atOrBefore.Month, 1);
                    for (int m = 0; m < 24; m++, month = month.AddMonths(-1))
                    {
                        DateTime? day = GetMonthlyDate(month);
                        if (day.HasValue && day.Value + Time <= atOrBefore) return day.Value + Time;
                    }
                    return null;
            }
        }

        private DateTime? GetMonthlyDate(DateTime firstOfMonth)
        {
            int daysInMonth = DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month);
            DateTime last = firstOfMonth.AddDays(daysInMonth - 1);
            switch (Kind)
            {
                case ScheduleKind.MonthlyDay:
                    return DayOfMonth <= daysInMonth ? firstOfMonth.AddDays(DayOfMonth - 1) : (DateTime?)null;
                case ScheduleKind.MonthlyLastDay:
                    return last;
                case ScheduleKind.MonthlyLastWeekday:
                    return last.AddDays(-((((int)last.DayOfWeek - (int)Weekday) + 7) % 7));
                default:
                    return null;
            }
        }

        public bool Equals(RestartSchedule other)
        {
            if (ReferenceEquals(other, null)) return false;
            if (Kind != other.Kind || Time != other.Time || WarningMinutes != other.WarningMinutes) return false;
            switch (Kind)
            {
                case ScheduleKind.Once: return Date == other.Date;
                case ScheduleKind.Weekly: return Days.SequenceEqual(other.Days);
                case ScheduleKind.MonthlyDay: return DayOfMonth == other.DayOfMonth;
                case ScheduleKind.MonthlyLastWeekday: return Weekday == other.Weekday;
                default: return true;
            }
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as RestartSchedule);
        }

        public override int GetHashCode()
        {
            return ((int)Kind * 397) ^ Time.GetHashCode() ^ WarningMinutes;
        }

        public override string ToString()
        {
            return HebrewText.Describe(this);
        }
    }
}
