using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart.Tests
{
    /// <summary>
    /// Foreign-task detection and management against the real Task Scheduler, inside a dedicated test
    /// folder that is always removed. Every test task is disabled or scheduled years ahead, and adopted
    /// copies are at least two hours away, so nothing ever runs.
    /// </summary>
    [TestClass]
    [TestCategory("Integration")]
    public class ForeignIntegrationTests
    {
        private const string Root = "ScheduledRestartForeignTest";
        private const string AppFolder = "ScheduledRestartTest";
        private const string AppTask = "ScheduledRestartTest";

        private string _tempDir;
        private ForeignTaskService _service;
        private ScheduleManager _app;
        private TaskSchedulerClient _appClient;
        private string _backupDir;

        [TestInitialize]
        public void Init()
        {
            DeleteTestTree();
            _tempDir = Path.Combine(Path.GetTempPath(), "SRForeign-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _backupDir = Path.Combine(_tempDir, "backups");
            var log = new RestartLog(Path.Combine(_tempDir, "test.log"), null);
            _appClient = new TaskSchedulerClient(AppFolder, AppTask);
            _appClient.Delete();
            _app = new ScheduleManager(_appClient, log, Path.Combine(_tempDir, "previous.xml"));
            _service = new ForeignTaskService("\\" + Root, p => false, _appClient.TaskPath, log, _backupDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            DeleteTestTree();
            _appClient.Delete();
            try { Directory.Delete(_tempDir, true); } catch (IOException) { }
        }

        [ClassCleanup]
        public static void FinalCleanup()
        {
            DeleteTestTree();
            new TaskSchedulerClient(AppFolder, AppTask).Delete();
        }

        private static TimeSpan SafeTime
        {
            get { return TimeSpan.FromHours((DateTime.Now.Hour + 3) % 24); }
        }

        private static string TaskXmlFor(string trigger, string command, string arguments, bool enabled, bool hidden = false)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<RegistrationInfo><Author>test</Author></RegistrationInfo><Triggers>" + trigger + "</Triggers>"
                + "<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
                + "<Settings><Enabled>" + (enabled ? "true" : "false") + "</Enabled><Hidden>" + (hidden ? "true" : "false") + "</Hidden>"
                + "<StartWhenAvailable>false</StartWhenAvailable></Settings>"
                + "<Actions Context=\"Author\"><Exec><Command>" + Escape(command) + "</Command>"
                + (arguments == null ? "" : "<Arguments>" + Escape(arguments) + "</Arguments>") + "</Exec></Actions></Task>";
        }

        private static string Escape(string s)
        {
            return System.Security.SecurityElement.Escape(s);
        }

        private static string WeeklyTrigger()
        {
            // Starts in 2035, so it cannot fire; the adopted copy uses today's date with a safe time.
            string day = DateTime.Today.AddDays(2).DayOfWeek.ToString();
            return "<CalendarTrigger><StartBoundary>2035-01-01T" + HebrewText.Time(SafeTime) + ":00</StartBoundary><Enabled>true</Enabled>"
                + "<ScheduleByWeek><DaysOfWeek><" + day + " /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger>";
        }

        private const string FarOnce = "<TimeTrigger><StartBoundary>2035-06-01T03:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger>";

        private static void Register(string folder, string name, string xml)
        {
            new TaskSchedulerClient(folder, name).Register(xml);
        }

        private void CreateForeignTasks()
        {
            string bat = Path.Combine(_tempDir, "nightly-reboot.bat");
            File.WriteAllText(bat, "@echo off\r\nrem nightly maintenance\r\nshutdown /r /f /t 0\r\n");

            Register(Root, "Weekly reboot", TaskXmlFor(WeeklyTrigger(), @"C:\Windows\System32\shutdown.exe", "/r /f /t 0", true));
            Register(Root, "Boot shutdown", TaskXmlFor("<BootTrigger><Enabled>true</Enabled></BootTrigger>", "shutdown.exe", "/s /t 0", false));
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("Restart-Computer -Force"));
            Register(Root, "Hidden encoded", TaskXmlFor("<LogonTrigger><Enabled>true</Enabled></LogonTrigger>", "powershell.exe", "-NoProfile -EncodedCommand " + encoded, false, true));
            Register(Root, "Cmd wrapper", TaskXmlFor(FarOnce, "cmd.exe", "/c shutdown /r /t 0", true));
            Register(Root, "Not power related", TaskXmlFor(FarOnce, "notepad.exe", null, true));
            Register(Root + "\\Sub", "Batch target", TaskXmlFor(FarOnce, bat, null, true));
        }

        private ForeignTask Find(ForeignScanResult scan, string name)
        {
            ForeignTask task = scan.Tasks.FirstOrDefault(t => t.Name == name);
            Assert.IsNotNull(task, name + " not found. Found: " + string.Join(", ", scan.Tasks.Select(t => t.Path)));
            return task;
        }

        [TestMethod]
        public void Scan_FindsEveryShape()
        {
            CreateForeignTasks();
            ForeignScanResult scan = _service.Scan();

            Assert.AreEqual(5, scan.Tasks.Count, string.Join(", ", scan.Tasks.Select(t => t.Path)));
            Assert.IsFalse(scan.Tasks.Any(t => t.Name == "Not power related"));

            ForeignTask weekly = Find(scan, "Weekly reboot");
            Assert.AreEqual(PowerAction.Restart, weekly.Action);
            Assert.IsTrue(weekly.Enabled);
            Assert.IsTrue(weekly.CanAdopt);
            Assert.AreEqual("\\" + Root + "\\Weekly reboot", weekly.Path);
            StringAssert.Contains(weekly.Account, "SYSTEM");
            StringAssert.Contains(weekly.CommandLine, "shutdown.exe /r /f /t 0");

            ForeignTask boot = Find(scan, "Boot shutdown");
            Assert.AreEqual(PowerAction.Shutdown, boot.Action);
            Assert.IsFalse(boot.Enabled);
            Assert.AreEqual("בכל הפעלת מחשב", boot.ScheduleText);
            Assert.IsFalse(boot.CanAdopt);

            ForeignTask hidden = Find(scan, "Hidden encoded");
            Assert.AreEqual(PowerAction.Restart, hidden.Action);
            Assert.AreEqual("בכניסת משתמש", hidden.ScheduleText);

            Assert.AreEqual(PowerAction.Restart, Find(scan, "Cmd wrapper").Action);

            ForeignTask batch = Find(scan, "Batch target");
            Assert.AreEqual(PowerAction.Restart, batch.Action);
            Assert.IsTrue(batch.DetectedInScript);
            Assert.AreEqual("\\" + Root + "\\Sub\\Batch target", batch.Path);
        }

        [TestMethod]
        public void DefaultScan_SkipsMicrosoftAndTestFolders()
        {
            Assert.IsTrue(ForeignTaskService.IsSkippedFolder("\\Microsoft"));
            Assert.IsTrue(ForeignTaskService.IsSkippedFolder("\\Microsoft\\Windows\\UpdateOrchestrator"));
            Assert.IsTrue(ForeignTaskService.IsSkippedFolder("\\ScheduledRestartTest"));
            Assert.IsFalse(ForeignTaskService.IsSkippedFolder("\\MicrosoftEdgeUpdate"));
            Assert.IsFalse(ForeignTaskService.IsSkippedFolder("\\" + Root));

            // A full scan must work on a real machine and never list the app's own task.
            ForeignScanResult all = ForeignTaskService.CreateDefault(new RestartLog(Path.Combine(_tempDir, "x.log"), null)).Scan();
            Assert.IsFalse(all.Tasks.Any(t => t.Path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(all.Tasks.Any(t => t.Path.Equals("\\ScheduledRestart\\ScheduledRestart", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void DisableAndEnable_AreVerified()
        {
            CreateForeignTasks();
            ForeignTask weekly = Find(_service.Scan(), "Weekly reboot");
            _service.SetEnabled(weekly, false);
            Assert.IsFalse(Find(_service.Scan(), "Weekly reboot").Enabled);
            _service.SetEnabled(weekly, true);
            Assert.IsTrue(Find(_service.Scan(), "Weekly reboot").Enabled);
        }

        [TestMethod]
        public void Delete_SavesBackupAndRemovesTask()
        {
            CreateForeignTasks();
            ForeignTask wrapper = Find(_service.Scan(), "Cmd wrapper");
            string backup = _service.Delete(wrapper);

            Assert.IsTrue(File.Exists(backup), backup);
            Assert.AreEqual(_backupDir, Path.GetDirectoryName(backup));
            StringAssert.StartsWith(Path.GetFileName(backup), "Cmd wrapper-");
            StringAssert.Contains(File.ReadAllText(backup, Encoding.Unicode), "/c shutdown /r /t 0");
            Assert.IsNull(TaskSchedulerClient.GetStateAt(wrapper.Path));
            Assert.IsFalse(_service.Scan().Tasks.Any(t => t.Name == "Cmd wrapper"));
        }

        [TestMethod]
        public void Adopt_CreatesAppTaskAndDisablesOriginal()
        {
            CreateForeignTasks();
            ForeignTask weekly = Find(_service.Scan(), "Weekly reboot");
            AdoptResult result = _service.Adopt(weekly, _app);

            Assert.IsTrue(result.Success, result.Error);
            ScheduleStatus status = _app.GetStatus();
            Assert.AreEqual(TaskPresence.Active, status.Presence);
            Assert.AreEqual(weekly.Schedule, status.Schedule);
            Assert.IsTrue(status.NextRun.HasValue && status.NextRun.Value > DateTime.Now.AddMinutes(60), "safety");
            RegisteredTaskState original = TaskSchedulerClient.GetStateAt(weekly.Path);
            Assert.IsNotNull(original, "original kept");
            Assert.IsFalse(original.Enabled, "original disabled");
        }

        [TestMethod]
        public void Adopt_ForcedFailure_RestoresPreviousState()
        {
            CreateForeignTasks();
            RestartSchedule existing = RestartSchedule.Daily(SafeTime);
            Assert.AreEqual(SaveOutcome.Saved, _app.Save(existing).Outcome);
            ForeignTask weekly = Find(_service.Scan(), "Weekly reboot");
            _service.BeforeDisableOriginalForTesting = () => { throw new InvalidOperationException("forced failure"); };

            AdoptResult result = _service.Adopt(weekly, _app);

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "forced failure");
            Assert.AreEqual(existing, _app.GetStatus().Schedule, "app task restored");
            Assert.IsTrue(TaskSchedulerClient.GetStateAt(weekly.Path).Enabled, "original still enabled");
        }

        [TestMethod]
        public void Adopt_ForcedFailure_WithoutPreviousAppTask_LeavesNone()
        {
            CreateForeignTasks();
            ForeignTask weekly = Find(_service.Scan(), "Weekly reboot");
            _service.BeforeDisableOriginalForTesting = () => { throw new InvalidOperationException("forced failure"); };
            Assert.IsFalse(_service.Adopt(weekly, _app).Success);
            Assert.AreEqual(TaskPresence.None, _app.GetStatus().Presence);
            Assert.IsTrue(TaskSchedulerClient.GetStateAt(weekly.Path).Enabled);
        }

        [TestMethod]
        public void RemoveEverything_OnTestRoot()
        {
            const string source = "ScheduledRestartTestSource";
            if (!EventLog.SourceExists(source)) EventLog.CreateEventSource(source, "Application");
            string dataDir = Path.Combine(_tempDir, "data");
            Directory.CreateDirectory(Path.Combine(dataDir, "backups"));
            File.WriteAllText(Path.Combine(dataDir, "ScheduledRestart.log"), "x");
            File.WriteAllText(Path.Combine(dataDir, "backups", "a.xml"), "x");
            Assert.AreEqual(SaveOutcome.Saved, _app.Save(RestartSchedule.Daily(SafeTime)).Outcome);
            CreateForeignTasks();

            var problems = Uninstaller.RemoveAll(_appClient, dataDir, source);

            Assert.AreEqual(0, problems.Count, string.Join(" | ", problems));
            Assert.IsNull(_appClient.GetTaskXml());
            Assert.IsFalse(Directory.Exists(dataDir));
            Assert.IsFalse(EventLog.SourceExists(source));
            Assert.AreEqual(5, _service.Scan().Tasks.Count, "foreign tasks untouched");
        }

        [TestMethod]
        public void BitLocker_RealQuery_DoesNotThrowAndDoesNotWarn()
        {
            string error;
            bool warn = BitLocker.ShouldWarn(out error);
            Console.WriteLine("BitLocker check error (null = query succeeded): " + (error ?? "null"));
            Assert.IsFalse(warn, "the CI runner has no pre-boot BitLocker protector");
        }

        /// <summary>Deletes every task and folder under the test root (any depth).</summary>
        private static void DeleteTestTree()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            dynamic root = service.GetFolder("\\");
            dynamic folder;
            try { folder = service.GetFolder("\\" + Root); }
            catch (Exception ex) when (TaskSchedulerClient.IsNotFound(ex)) { return; }
            DeleteFolderContents(folder);
            root.DeleteFolder(Root, 0);
        }

        private static void DeleteFolderContents(dynamic folder)
        {
            dynamic subs = folder.GetFolders(0);
            for (int i = (int)subs.Count; i >= 1; i--)
            {
                dynamic sub = subs.Item(i);
                DeleteFolderContents(sub);
                folder.DeleteFolder((string)sub.Name, 0);
            }
            dynamic tasks = folder.GetTasks(1);
            for (int i = (int)tasks.Count; i >= 1; i--)
                folder.DeleteTask((string)tasks.Item(i).Name, 0);
        }
    }
}
