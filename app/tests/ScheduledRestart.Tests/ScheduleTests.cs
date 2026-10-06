using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScheduledRestart.Core;

namespace ScheduledRestart.Tests
{
    [TestClass]
    public class NextRunTests
    {
        // Tuesday 2026-10-06 10:00
        private static readonly DateTime Now = new DateTime(2026, 10, 6, 10, 0, 0);
        private static readonly TimeSpan Three = new TimeSpan(3, 0, 0);

        [TestMethod]
        public void Once_InFuture_ReturnsDate()
        {
            Assert.AreEqual(new DateTime(2026, 10, 9, 3, 0, 0), RestartSchedule.Once(new DateTime(2026, 10, 9), Three).GetNextRun(Now));
        }

        [TestMethod]
        public void Once_InPast_ReturnsNull()
        {
            Assert.IsNull(RestartSchedule.Once(new DateTime(2026, 10, 6), Three).GetNextRun(Now));
        }

        [TestMethod]
        public void Daily_TimePassed_ReturnsTomorrow()
        {
            Assert.AreEqual(new DateTime(2026, 10, 7, 3, 0, 0), RestartSchedule.Daily(Three).GetNextRun(Now));
        }

        [TestMethod]
        public void Daily_TimeAhead_ReturnsToday()
        {
            Assert.AreEqual(new DateTime(2026, 10, 6, 22, 30, 0), RestartSchedule.Daily(new TimeSpan(22, 30, 0)).GetNextRun(Now));
        }

        [TestMethod]
        public void Weekly_PicksNearestDay()
        {
            var s = RestartSchedule.Weekly(new[] { DayOfWeek.Friday, DayOfWeek.Sunday }, Three);
            Assert.AreEqual(new DateTime(2026, 10, 9, 3, 0, 0), s.GetNextRun(Now));
        }

        [TestMethod]
        public void Weekly_SameDayTimePassed_ReturnsNextWeek()
        {
            var s = RestartSchedule.Weekly(new[] { DayOfWeek.Tuesday }, Three);
            Assert.AreEqual(new DateTime(2026, 10, 13, 3, 0, 0), s.GetNextRun(Now));
        }

        [TestMethod]
        public void Weekly_SameDayTimeAhead_ReturnsToday()
        {
            var s = RestartSchedule.Weekly(new[] { DayOfWeek.Tuesday }, new TimeSpan(23, 0, 0));
            Assert.AreEqual(new DateTime(2026, 10, 6, 23, 0, 0), s.GetNextRun(Now));
        }

        [TestMethod]
        public void MonthlyDay_31_SkipsShortMonths()
        {
            // October has 31 days; from 31/10 after the run, November (30) is skipped.
            var s = RestartSchedule.MonthlyDay(31, Three);
            Assert.AreEqual(new DateTime(2026, 10, 31, 3, 0, 0), s.GetNextRun(Now));
            Assert.AreEqual(new DateTime(2026, 12, 31, 3, 0, 0), s.GetNextRun(new DateTime(2026, 10, 31, 4, 0, 0)));
        }

        [TestMethod]
        public void MonthlyDay_29_SkipsNonLeapFebruary()
        {
            var s = RestartSchedule.MonthlyDay(29, Three);
            Assert.AreEqual(new DateTime(2027, 3, 29, 3, 0, 0), s.GetNextRun(new DateTime(2027, 1, 30)));
            Assert.AreEqual(new DateTime(2028, 2, 29, 3, 0, 0), s.GetNextRun(new DateTime(2028, 1, 30)));
        }

        [TestMethod]
        public void MonthlyDay_TodayPassed_ReturnsNextMonth()
        {
            Assert.AreEqual(new DateTime(2026, 11, 6, 3, 0, 0), RestartSchedule.MonthlyDay(6, Three).GetNextRun(Now));
        }

        [TestMethod]
        public void MonthlyLastDay()
        {
            var s = RestartSchedule.MonthlyLastDay(Three);
            Assert.AreEqual(new DateTime(2026, 10, 31, 3, 0, 0), s.GetNextRun(Now));
            Assert.AreEqual(new DateTime(2027, 2, 28, 3, 0, 0), s.GetNextRun(new DateTime(2027, 2, 1)));
            Assert.AreEqual(new DateTime(2028, 2, 29, 3, 0, 0), s.GetNextRun(new DateTime(2028, 2, 1)));
        }

        [TestMethod]
        public void MonthlyLastWeekday()
        {
            Assert.AreEqual(new DateTime(2026, 10, 30, 3, 0, 0), RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three).GetNextRun(Now));
            Assert.AreEqual(new DateTime(2026, 10, 25, 3, 0, 0), RestartSchedule.MonthlyLastWeekday(DayOfWeek.Sunday, Three).GetNextRun(Now));
            // Last Saturday of October 2026 is the 31st.
            Assert.AreEqual(new DateTime(2026, 10, 31, 3, 0, 0), RestartSchedule.MonthlyLastWeekday(DayOfWeek.Saturday, Three).GetNextRun(Now));
        }

        [TestMethod]
        public void MonthlyLastWeekday_PassedThisMonth_ReturnsNextMonth()
        {
            Assert.AreEqual(new DateTime(2026, 11, 27, 3, 0, 0),
                RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three).GetNextRun(new DateTime(2026, 10, 30, 3, 0, 0)));
        }

        [TestMethod]
        public void Equality_ComparesRelevantFields()
        {
            Assert.AreEqual(RestartSchedule.Weekly(new[] { DayOfWeek.Friday, DayOfWeek.Monday }, Three),
                RestartSchedule.Weekly(new[] { DayOfWeek.Monday, DayOfWeek.Friday, DayOfWeek.Friday }, Three));
            Assert.AreNotEqual(RestartSchedule.Daily(Three, 5), RestartSchedule.Daily(Three, 0));
            Assert.AreNotEqual(RestartSchedule.MonthlyDay(5, Three), RestartSchedule.MonthlyDay(6, Three));
        }
    }

    [TestClass]
    public class HebrewTextTests
    {
        private static readonly TimeSpan Three = new TimeSpan(3, 0, 0);

        [TestMethod]
        public void Weekly_SingleDay()
        {
            Assert.AreEqual("כל שבוע ביום שישי בשעה 03:00", HebrewText.Describe(RestartSchedule.Weekly(new[] { DayOfWeek.Friday }, Three)));
        }

        [TestMethod]
        public void Weekly_SeveralDays_SundayFirst()
        {
            Assert.AreEqual("כל שבוע בימים ראשון, שלישי ושישי בשעה 03:00",
                HebrewText.Describe(RestartSchedule.Weekly(new[] { DayOfWeek.Friday, DayOfWeek.Sunday, DayOfWeek.Tuesday }, Three)));
        }

        [TestMethod]
        public void AllKinds()
        {
            Assert.AreEqual("חד־פעמי ביום שישי, 09/10/2026 בשעה 03:00", HebrewText.Describe(RestartSchedule.Once(new DateTime(2026, 10, 9), Three)));
            Assert.AreEqual("כל יום בשעה 22:15", HebrewText.Describe(RestartSchedule.Daily(new TimeSpan(22, 15, 0))));
            Assert.AreEqual("כל חודש ב־15 לחודש בשעה 03:00", HebrewText.Describe(RestartSchedule.MonthlyDay(15, Three)));
            Assert.AreEqual("כל חודש ביום האחרון בחודש בשעה 03:00", HebrewText.Describe(RestartSchedule.MonthlyLastDay(Three)));
            Assert.AreEqual("כל חודש ביום שישי האחרון בחודש בשעה 03:00", HebrewText.Describe(RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three)));
        }

        [TestMethod]
        public void NextRunLine()
        {
            Assert.AreEqual("ההפעלה מחדש הבאה: יום שישי, 09/10/2026 בשעה 03:00", HebrewText.NextRunLine(new DateTime(2026, 10, 9, 3, 0, 0)));
            Assert.AreEqual("ההפעלה מחדש הבאה: אין מועד עתידי", HebrewText.NextRunLine(null));
        }

        [TestMethod]
        public void WarningTexts()
        {
            Assert.AreEqual("ללא", HebrewText.WarningOption(0));
            Assert.AreEqual("דקה", HebrewText.WarningOption(1));
            Assert.AreEqual("15 דקות", HebrewText.WarningOption(15));
            Assert.AreEqual("המחשב יופעל מחדש בעוד 5 דקות לצורך תחזוקה. נא לשמור את העבודה.", HebrewText.WarningMessage(5));
            Assert.AreEqual("המחשב יופעל מחדש בעוד דקה לצורך תחזוקה. נא לשמור את העבודה.", HebrewText.WarningMessage(1));
        }

        [TestMethod]
        public void TaskResults()
        {
            Assert.AreEqual("הצליחה", HebrewText.TaskResult(0));
            Assert.AreEqual("קוד 0x8004131F", HebrewText.TaskResult(unchecked((int)0x8004131F)));
        }
    }
}
