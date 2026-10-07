using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScheduledRestart.Core;

namespace ScheduledRestart.Tests
{
    [TestClass]
    public class TaskXmlTests
    {
        private static readonly TimeSpan Three = new TimeSpan(3, 0, 0);
        private static readonly DateTime Today = new DateTime(2026, 10, 6);

        public static IEnumerable<object[]> AllSchedules()
        {
            foreach (int warning in RestartSchedule.WarningOptions)
            {
                yield return new object[] { RestartSchedule.Once(new DateTime(2026, 12, 31), new TimeSpan(23, 59, 0), warning) };
                yield return new object[] { RestartSchedule.Daily(Three, warning) };
                yield return new object[] { RestartSchedule.Weekly(new[] { DayOfWeek.Sunday, DayOfWeek.Wednesday, DayOfWeek.Saturday }, Three, warning) };
                yield return new object[] { RestartSchedule.MonthlyDay(15, Three, warning) };
                yield return new object[] { RestartSchedule.MonthlyDay(31, new TimeSpan(0, 5, 0), warning) };
                yield return new object[] { RestartSchedule.MonthlyLastDay(Three, warning) };
                yield return new object[] { RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three, warning) };
            }
        }

        [DataTestMethod]
        [DynamicData(nameof(AllSchedules), DynamicDataSourceType.Method)]
        public void RoundTrip(RestartSchedule schedule)
        {
            string xml = TaskXml.Build(schedule, "TEST\\user", "תיאור", Today);
            TaskXmlInfo info = TaskXml.Parse(xml);
            Assert.IsTrue(info.IsRecognized, xml);
            Assert.AreEqual(schedule, info.Schedule);
            Assert.AreEqual("S-1-5-18", info.UserId);
            Assert.AreEqual("HighestAvailable", info.RunLevel);
            Assert.IsTrue(info.Enabled);
            Assert.IsFalse(info.WakeToRun);
            Assert.IsFalse(info.StartWhenAvailable);
            Assert.AreEqual(AppConstants.ShutdownExe, info.Command);
            Assert.AreEqual(TaskXml.BuildShutdownArguments(schedule.WarningMinutes), info.Arguments);
        }

        [TestMethod]
        public void Build_UsesDocumentedLastElements()
        {
            StringAssert.Contains(TaskXml.Build(RestartSchedule.MonthlyLastDay(Three), "u", "d", Today), "<Day>Last</Day>");
            string xml = TaskXml.Build(RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three), "u", "d", Today);
            StringAssert.Contains(xml, "<Week>Last</Week>");
            StringAssert.Contains(xml, "<Friday />");
        }

        [TestMethod]
        public void Build_SettingsMatchScript()
        {
            string xml = TaskXml.Build(RestartSchedule.Daily(Three), "u", "d", Today);
            StringAssert.Contains(xml, "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            StringAssert.Contains(xml, "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            StringAssert.Contains(xml, "<StartWhenAvailable>false</StartWhenAvailable>");
            StringAssert.Contains(xml, "<WakeToRun>false</WakeToRun>");
            StringAssert.Contains(xml, "<ExecutionTimeLimit>PT10M</ExecutionTimeLimit>");
            StringAssert.Contains(xml, "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            StringAssert.Contains(xml, "<StartBoundary>2026-10-06T03:00:00</StartBoundary>");
        }

        [TestMethod]
        public void ShutdownArguments()
        {
            Assert.AreEqual("/r /f /t 0", TaskXml.BuildShutdownArguments(0));
            Assert.AreEqual("/r /f /t 300 /c \"המחשב יופעל מחדש בעוד 5 דקות לצורך תחזוקה. נא לשמור את העבודה.\"", TaskXml.BuildShutdownArguments(5));
            int m;
            Assert.IsTrue(TaskXml.TryParseWarningMinutes("/r /f /t 0 /d p:4:1 /c \"Scheduled restart\"", out m));
            Assert.AreEqual(0, m);
            Assert.IsTrue(TaskXml.TryParseWarningMinutes("/r /f /t 900 /c \"x\"", out m));
            Assert.AreEqual(15, m);
            Assert.IsFalse(TaskXml.TryParseWarningMinutes("/s /f /t 0", out m), "shutdown without restart");
            Assert.IsFalse(TaskXml.TryParseWarningMinutes("/r /t 45", out m), "unsupported delay");
        }

        // XML as Task Scheduler stores tasks created by Set-ScheduledRestart.ps1 (COM weekly trigger).
        internal const string ScriptWeeklyXml = @"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>PC\admin</Author>
    <Description>Automatic restart created by PC\admin on 2026-09-24 10:00:00 with Set-ScheduledRestart.ps1 v1.0.0. Schedule: Every week on Monday and Friday at 03:00.</Description>
    <URI>\ScheduledRestart\ScheduledRestart</URI>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>2026-09-24T03:00:00</StartBoundary>
      <Enabled>true</Enabled>
      <ScheduleByWeek>
        <DaysOfWeek>
          <Monday />
          <Friday />
        </DaysOfWeek>
        <WeeksInterval>1</WeeksInterval>
      </ScheduleByWeek>
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>true</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>false</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>true</WakeToRun>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>C:\Windows\System32\shutdown.exe</Command>
      <Arguments>/r /f /t 0 /d p:4:1 /c ""Scheduled restart""</Arguments>
    </Exec>
  </Actions>
</Task>";

        [TestMethod]
        public void Parse_ScriptCreatedWeeklyTask()
        {
            TaskXmlInfo info = TaskXml.Parse(ScriptWeeklyXml);
            Assert.AreEqual(RestartSchedule.Weekly(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, Three, 0), info.Schedule);
            Assert.IsFalse(info.Enabled, "paused in Settings");
        }

        [TestMethod]
        public void Parse_ScriptCreatedMonthlyTasks()
        {
            string day15 = Replace(@"<ScheduleByMonth><DaysOfMonth><Day>15</Day></DaysOfMonth><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonth>");
            Assert.AreEqual(RestartSchedule.MonthlyDay(15, Three), TaskXml.Parse(day15).Schedule);
            string last = Replace(@"<ScheduleByMonth><DaysOfMonth><Day>Last</Day></DaysOfMonth><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonth>");
            Assert.AreEqual(RestartSchedule.MonthlyLastDay(Three), TaskXml.Parse(last).Schedule);
            string lastFriday = Replace(@"<ScheduleByMonthDayOfWeek><Weeks><Week>Last</Week></Weeks><DaysOfWeek><Friday /></DaysOfWeek><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonthDayOfWeek>");
            Assert.AreEqual(RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three), TaskXml.Parse(lastFriday).Schedule);
        }

        [TestMethod]
        public void Parse_ScriptOneTimeTask()
        {
            string xml = ScriptWeeklyXml.Replace(Between(ScriptWeeklyXml, "<Triggers>", "</Triggers>"),
                "<Triggers><TimeTrigger><StartBoundary>2026-12-31T23:59:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>");
            Assert.AreEqual(RestartSchedule.Once(new DateTime(2026, 12, 31), new TimeSpan(23, 59, 0)), TaskXml.Parse(xml).Schedule);
        }

        [TestMethod]
        public void Parse_UnsupportedTasks_AreUnrecognized()
        {
            string secondTuesday = Replace(@"<ScheduleByMonthDayOfWeek><Weeks><Week>2</Week></Weeks><DaysOfWeek><Tuesday /></DaysOfWeek><Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months></ScheduleByMonthDayOfWeek>");
            Assert.IsFalse(TaskXml.Parse(secondTuesday).IsRecognized, "Nth weekday other than last");
            string someMonths = Replace(@"<ScheduleByMonth><DaysOfMonth><Day>1</Day></DaysOfMonth><Months><January /></Months></ScheduleByMonth>");
            Assert.IsFalse(TaskXml.Parse(someMonths).IsRecognized, "not every month");
            string everyTwoWeeks = ScriptWeeklyXml.Replace("<WeeksInterval>1</WeeksInterval>", "<WeeksInterval>2</WeeksInterval>");
            Assert.IsFalse(TaskXml.Parse(everyTwoWeeks).IsRecognized, "every two weeks");
            string otherAction = ScriptWeeklyXml.Replace(@"C:\Windows\System32\shutdown.exe", @"C:\Windows\notepad.exe");
            Assert.IsFalse(TaskXml.Parse(otherAction).IsRecognized, "not shutdown.exe");
            string twoTriggers = ScriptWeeklyXml.Replace("</Triggers>", "<TimeTrigger><StartBoundary>2026-12-31T23:59:00</StartBoundary></TimeTrigger></Triggers>");
            Assert.IsFalse(TaskXml.Parse(twoTriggers).IsRecognized, "two triggers");
            string withSeconds = ScriptWeeklyXml.Replace("T03:00:00", "T03:00:30");
            Assert.IsFalse(TaskXml.Parse(withSeconds).IsRecognized, "seconds in start time");
        }

        [TestMethod]
        public void Parse_StartBoundaryWithTimeZone()
        {
            string xml = ScriptWeeklyXml.Replace("2026-09-24T03:00:00", "2026-09-24T03:00:00+03:00");
            Assert.IsTrue(TaskXml.Parse(xml).IsRecognized);
        }

        private static string Replace(string scheduleElement)
        {
            return ScriptWeeklyXml.Replace(Between(ScriptWeeklyXml, "<ScheduleByWeek>", "</ScheduleByWeek>"), scheduleElement);
        }

        private static string Between(string text, string start, string end)
        {
            int s = text.IndexOf(start, StringComparison.Ordinal);
            int e = text.IndexOf(end, s, StringComparison.Ordinal) + end.Length;
            return text.Substring(s, e - s);
        }
    }
}
