using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart.Tests
{
    /// <summary>
    /// Runs against the real Task Scheduler (elevated, Windows only) under a separate test task.
    /// Every schedule is at least two hours away and the test task is always deleted, so the
    /// shutdown.exe action never runs.
    /// </summary>
    [TestClass]
    [TestCategory("Integration")]
    public class IntegrationTests
    {
        private const string TestFolder = "ScheduledRestartTest";
        private const string TestTask = "ScheduledRestartTest";

        private string _tempDir;
        private TaskSchedulerClient _client;
        private ScheduleManager _manager;
        private string _logPath;
        private string _backupPath;

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ScheduledRestartTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _logPath = Path.Combine(_tempDir, "test.log");
            _backupPath = Path.Combine(_tempDir, "previous.xml");
            _client = new TaskSchedulerClient(TestFolder, TestTask);
            _manager = new ScheduleManager(_client, new RestartLog(_logPath, null), _backupPath);
            _client.Delete();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _client.Delete();
            Assert.IsNull(_client.GetTaskXml(), "test task must be gone after cleanup");
            try { Directory.Delete(_tempDir, true); } catch (IOException) { }
        }

        [ClassCleanup]
        public static void FinalCleanup()
        {
            new TaskSchedulerClient(TestFolder, TestTask).Delete();
        }

        private static TimeSpan SafeTime
        {
            get { return TimeSpan.FromHours((DateTime.Now.Hour + 3) % 24); }
        }

        private static RestartSchedule[] AllKinds()
        {
            DayOfWeek tomorrow = DateTime.Today.AddDays(1).DayOfWeek;
            DayOfWeek inThreeDays = DateTime.Today.AddDays(3).DayOfWeek;
            return new[]
            {
                RestartSchedule.Once(DateTime.Today.AddDays(7), SafeTime),
                RestartSchedule.Daily(SafeTime),
                RestartSchedule.Weekly(new[] { tomorrow, inThreeDays }, SafeTime),
                RestartSchedule.MonthlyDay(15, SafeTime),
                RestartSchedule.MonthlyLastDay(SafeTime),
                RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, SafeTime, 10)
            };
        }

        /// <summary>Fails (and deletes the task) if Task Scheduler would run it within the next hour.</summary>
        private ScheduleStatus SafeStatus()
        {
            ScheduleStatus status = _manager.GetStatus();
            if (status.NextRun.HasValue && status.NextRun.Value < DateTime.Now.AddMinutes(60))
            {
                _client.Delete();
                Assert.Fail("SAFETY: next run " + status.NextRun + " is less than an hour away; task deleted.");
            }
            return status;
        }

        [TestMethod]
        public void Save_EveryKind_IsVerified()
        {
            foreach (RestartSchedule schedule in AllKinds())
            {
                SaveResult result = _manager.Save(schedule);
                Assert.AreEqual(SaveOutcome.Saved, result.Outcome, schedule + ": " + result.Error);
                ScheduleStatus status = SafeStatus();
                Assert.AreEqual(TaskPresence.Active, status.Presence, schedule.ToString());
                Assert.AreEqual(schedule, status.Schedule);
                Assert.AreEqual(schedule.GetNextRun(DateTime.Now), status.NextRun, "next run agrees with Task Scheduler: " + schedule);
                _client.Delete();
            }
        }

        [TestMethod]
        public void Save_WithWarning_StoresHebrewMessage()
        {
            Assert.AreEqual(SaveOutcome.Saved, _manager.Save(RestartSchedule.Daily(SafeTime, 5)).Outcome);
            SafeStatus();
            TaskXmlInfo info = TaskXml.Parse(_client.GetTaskXml());
            Assert.AreEqual(TaskXml.BuildShutdownArguments(5), info.Arguments);
            StringAssert.Contains(info.Arguments, "/t 300");
            StringAssert.Contains(info.Arguments, HebrewText.WarningMessage(5));
            Assert.AreEqual(5, info.Schedule.WarningMinutes);
        }

        [TestMethod]
        public void PauseAndResume()
        {
            RestartSchedule schedule = RestartSchedule.Daily(SafeTime);
            Assert.AreEqual(SaveOutcome.Saved, _manager.Save(schedule).Outcome);
            _manager.SetEnabled(false);
            ScheduleStatus paused = SafeStatus();
            Assert.AreEqual(TaskPresence.Paused, paused.Presence);
            Assert.AreEqual(schedule, paused.Schedule);
            _manager.SetEnabled(true);
            Assert.AreEqual(TaskPresence.Active, SafeStatus().Presence);
        }

        [TestMethod]
        public void Replace_BacksUpPreviousTask()
        {
            RestartSchedule first = RestartSchedule.Weekly(new[] { DateTime.Today.AddDays(2).DayOfWeek }, SafeTime);
            RestartSchedule second = RestartSchedule.MonthlyLastDay(SafeTime);
            Assert.AreEqual(SaveOutcome.Saved, _manager.Save(first).Outcome);
            SaveResult result = _manager.Save(second);
            Assert.AreEqual(SaveOutcome.Saved, result.Outcome, result.Error);
            Assert.AreEqual(second, SafeStatus().Schedule);
            Assert.IsTrue(File.Exists(_backupPath));
            Assert.AreEqual(first, TaskXml.Parse(File.ReadAllText(_backupPath, Encoding.Unicode)).Schedule);
        }

        [TestMethod]
        public void Rollback_OnVerificationMismatch_RestoresPrevious()
        {
            RestartSchedule original = RestartSchedule.MonthlyDay(15, SafeTime);
            Assert.AreEqual(SaveOutcome.Saved, _manager.Save(original).Outcome);
            string previousXml = _client.GetTaskXml();

            // Register a daily task but expect a weekly one: verification must fail and roll back.
            string xml = TaskXml.Build(RestartSchedule.Daily(SafeTime), "test", "test", DateTime.Today);
            SaveResult result = _manager.Apply(xml, RestartSchedule.Weekly(new[] { DayOfWeek.Monday }, SafeTime), previousXml);

            Assert.AreEqual(SaveOutcome.FailedRestored, result.Outcome);
            Assert.IsFalse(string.IsNullOrEmpty(result.Error));
            ScheduleStatus status = SafeStatus();
            Assert.AreEqual(TaskPresence.Active, status.Presence);
            Assert.AreEqual(original, status.Schedule);
        }

        [TestMethod]
        public void Rollback_OnRegistrationError_RestoresPrevious()
        {
            RestartSchedule original = RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, SafeTime);
            Assert.AreEqual(SaveOutcome.Saved, _manager.Save(original).Outcome);
            _manager.SetEnabled(false);
            string previousXml = _client.GetTaskXml();

            SaveResult result = _manager.Apply("<Task xmlns=\"" + TaskXml.Namespace + "\"><Broken /></Task>", original, previousXml);

            Assert.AreEqual(SaveOutcome.FailedRestored, result.Outcome);
            ScheduleStatus status = SafeStatus();
            Assert.AreEqual(TaskPresence.Paused, status.Presence, "restored exactly as it was, including paused state");
            Assert.AreEqual(original, status.Schedule);
        }

        [TestMethod]
        public void Failure_WithoutPreviousTask_LeavesNothing()
        {
            string xml = TaskXml.Build(RestartSchedule.Daily(SafeTime), "test", "test", DateTime.Today);
            SaveResult result = _manager.Apply(xml, RestartSchedule.Daily(SafeTime, 15), null);
            Assert.AreEqual(SaveOutcome.Failed, result.Outcome);
            Assert.AreEqual(TaskPresence.None, _manager.GetStatus().Presence);
        }

        [TestMethod]
        public void Delete_RemovesTaskAndEmptyFolder()
        {
            Assert.AreEqual(SaveOutcome.Saved, _manager.Save(RestartSchedule.Daily(SafeTime)).Outcome);
            _manager.Delete();
            Assert.AreEqual(TaskPresence.None, _manager.GetStatus().Presence);

            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            bool folderGone = false;
            try { service.GetFolder("\\" + TestFolder); }
            catch (System.Runtime.InteropServices.COMException) { folderGone = true; }
            Assert.IsTrue(folderGone, "empty task folder removed");
        }

        [TestMethod]
        public void ScriptStyleTask_IsRecognizedAndReplaceable()
        {
            string scriptXml = TaskXmlTests.ScriptWeeklyXml
                .Replace(@"<URI>\ScheduledRestart\ScheduledRestart</URI>", string.Empty)
                .Replace("2026-09-24T03:00:00", DateTime.Today.ToString("yyyy-MM-dd") + "T" + HebrewText.Time(SafeTime) + ":00");
            _client.Register(scriptXml);
            ScheduleStatus status = SafeStatus();
            Assert.AreEqual(TaskPresence.Paused, status.Presence);
            Assert.AreEqual(RestartSchedule.Weekly(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, SafeTime), status.Schedule);

            SaveResult result = _manager.Save(RestartSchedule.Daily(SafeTime));
            Assert.AreEqual(SaveOutcome.Saved, result.Outcome, result.Error);
            Assert.AreEqual(TaskPresence.Active, SafeStatus().Presence);
        }

        [TestMethod]
        public void Actions_AreLoggedInExpectedFormat()
        {
            RestartSchedule schedule = RestartSchedule.Daily(SafeTime);
            _manager.Save(schedule);
            _manager.SetEnabled(false);
            _manager.SetEnabled(true);
            _manager.Delete();

            string[] lines = File.ReadAllLines(_logPath, Encoding.UTF8);
            byte[] head = File.ReadAllBytes(_logPath).Take(3).ToArray();
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, head, "UTF-8 with BOM");
            Assert.IsTrue(lines.All(l => Regex.IsMatch(l, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \| .+ \| .+$")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.Contains("נוצר תזמון: " + HebrewText.Describe(schedule))));
            Assert.IsTrue(lines.Any(l => l.EndsWith("התזמון הושהה.")));
            Assert.IsTrue(lines.Any(l => l.EndsWith("התזמון חודש.")));
            Assert.IsTrue(lines.Any(l => l.Contains("התזמון בוטל")));
        }
    }
}
