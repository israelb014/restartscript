using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScheduledRestart.Core;

namespace ScheduledRestart.Tests
{
    [TestClass]
    public class CommandClassifierTests
    {
        private static string Enc(string script)
        {
            return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        public static IEnumerable<object[]> Cases()
        {
            // command, arguments, expected
            var r = PowerAction.Restart;
            var s = PowerAction.Shutdown;
            var n = PowerAction.None;
            return new[]
            {
                // shutdown.exe
                new object[] { "shutdown.exe", "/r /f /t 0", r },
                new object[] { "shutdown", "-r -t 60", r },
                new object[] { @"C:\Windows\System32\shutdown.exe", "/r /f /t 0 /d p:4:1 /c \"Scheduled restart\"", r },
                new object[] { "\"C:\\Windows\\System32\\SHUTDOWN.EXE\"", "/R /T 0", r },
                new object[] { @"%SystemRoot%\System32\shutdown.exe", "/g /t 0", r },
                new object[] { "shutdown", "/r /o /t 0", r },
                new object[] { "shutdown.exe", "/s /t 0", s },
                new object[] { "shutdown.exe", "-s -f", s },
                new object[] { "shutdown", "/p", s },
                new object[] { "shutdown", "/sg /t 0", s },
                new object[] { "shutdown.exe", "/a", n },
                new object[] { "shutdown.exe", "/l", n },
                new object[] { "shutdown.exe", "/h", n },
                new object[] { "shutdown.exe", "/?", n },
                new object[] { "shutdown.exe", "/i", n },
                new object[] { "shutdown.exe", "", n },
                new object[] { "shutdownx.exe", "/r", n },
                // psshutdown / wmic
                new object[] { "psshutdown.exe", "-r -t 0", r },
                new object[] { @"C:\Tools\PsShutdown64.exe", "-r -f", r },
                new object[] { "psshutdown", "-s -t 0", s },
                new object[] { "psshutdown", "-k", s },
                new object[] { "psshutdown", "-h", n },
                new object[] { "psshutdown", "-a", n },
                new object[] { "wmic", "os where primary=1 call reboot", r },
                new object[] { "wmic.exe", "/node:localhost os call reboot", r },
                new object[] { "wmic", "os get caption", n },
                // cmd.exe wrappers
                new object[] { "cmd.exe", "/c shutdown /r /t 0", r },
                new object[] { "cmd", "/C \"shutdown -r -f -t 30\"", r },
                new object[] { @"C:\Windows\System32\cmd.exe", "/c echo restarting & shutdown.exe /r /t 0", r },
                new object[] { "cmd.exe", "/c shutdown /s /t 0 && echo done", s },
                new object[] { "cmd.exe", "/k shutdown /a", n },
                new object[] { "cmd.exe", "/c start \"\" shutdown /r", r },
                new object[] { "cmd.exe", "/c echo shutdown /r", n },
                new object[] { "cmd.exe", "/c dir", n },
                // PowerShell inline
                new object[] { "powershell.exe", "-NoProfile -Command Restart-Computer -Force", r },
                new object[] { "powershell", "-c \"Restart-Computer\"", r },
                new object[] { "pwsh.exe", "-NoLogo -Command \"Stop-Computer -Force\"", s },
                new object[] { "powershell.exe", "-ExecutionPolicy Bypass -Command \"& 'C:\\Windows\\System32\\shutdown.exe' /r /t 0\"", r },
                new object[] { "powershell.exe", "-Command \"Start-Process shutdown -ArgumentList '/r /t 0'\"", r },
                new object[] { "powershell.exe", "Restart-Computer", r },
                new object[] { "powershell.exe", "-Command \"Get-Date; Write-Output ok\"", n },
                new object[] { "powershell.exe", "-Command \"Restart-Service spooler\"", n },
                new object[] { "powershell.exe", "-Command \"Invoke-CimMethod -ClassName Win32_OperatingSystem -MethodName Reboot\"", r },
                // PowerShell -EncodedCommand (Base64 UTF-16LE)
                new object[] { "powershell.exe", "-NoProfile -EncodedCommand " + Enc("Restart-Computer -Force"), r },
                new object[] { "powershell.exe", "-enc " + Enc("Stop-Computer"), s },
                new object[] { "pwsh", "-e " + Enc("shutdown.exe /r /t 0"), r },
                new object[] { "powershell.exe", "-ec " + Enc("Get-Process"), n },
                new object[] { "powershell.exe", "-EncodedCommand not-base64!!", n },
                // Not power related
                new object[] { "notepad.exe", "", n },
                new object[] { "C:\\Program Files\\Backup\\backup.exe", "/restart-service", n },
                new object[] { "schtasks.exe", "/run /tn x", n },
                new object[] { "", "", n }
            };
        }

        [DataTestMethod]
        [DynamicData(nameof(Cases), DynamicDataSourceType.Method)]
        public void Classify(string command, string arguments, PowerAction expected)
        {
            Classification c = CommandClassifier.Classify(command, arguments, null, (p, w) => null);
            Assert.AreEqual(expected, c.Action, command + " " + arguments);
            Assert.IsFalse(c.DetectedInScript);
        }

        [TestMethod]
        public void HasAtLeastFortyCases()
        {
            Assert.IsTrue(new List<object[]>(Cases()).Count >= 40);
        }

        [TestMethod]
        public void DecodeEncodedCommand()
        {
            Assert.AreEqual("Restart-Computer -Force", CommandClassifier.DecodeEncodedCommand(Enc("Restart-Computer -Force")));
            Assert.IsNull(CommandClassifier.DecodeEncodedCommand("%%%"));
        }

        [TestMethod]
        public void ProgramName_Normalizes()
        {
            Assert.AreEqual("shutdown", CommandClassifier.ProgramName("\"C:\\Windows\\System32\\SHUTDOWN.EXE\""));
            Assert.AreEqual("pwsh", CommandClassifier.ProgramName("C:/Program Files/PowerShell/7/pwsh.exe"));
        }
    }

    [TestClass]
    public class ScriptDetectionTests
    {
        private string _dir;

        [TestInitialize]
        public void Init()
        {
            _dir = Path.Combine(Path.GetTempPath(), "srscript-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Directory.Delete(_dir, true);
        }

        private string Write(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static Classification Run(string command, string arguments, string workingDirectory = null)
        {
            return CommandClassifier.Classify(command, arguments, workingDirectory, ScriptReader.TryRead);
        }

        [TestMethod]
        public void BatchFile_WithRestart_IsDetectedInsideScript()
        {
            string bat = Write("nightly.bat", "@echo off\r\nREM shutdown /s is only a comment\r\necho bye\r\nshutdown /r /f /t 0\r\n");
            Classification c = Run(bat, "");
            Assert.AreEqual(PowerAction.Restart, c.Action);
            Assert.IsTrue(c.DetectedInScript);
        }

        [TestMethod]
        public void CmdWrapper_CallsBatch()
        {
            string cmd = Write("off.cmd", ":: weekly\r\n@shutdown.exe -s -t 0\r\n");
            Classification c = Run("cmd.exe", "/c \"" + cmd + "\"");
            Assert.AreEqual(PowerAction.Shutdown, c.Action);
            Assert.IsTrue(c.DetectedInScript);
        }

        [TestMethod]
        public void PowerShellFile_IsRead()
        {
            string ps1 = Write("reboot.ps1", "# reboot the box\r\nWrite-Output 'bye'\r\nRestart-Computer -Force\r\n");
            Classification c = Run("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\"");
            Assert.AreEqual(PowerAction.Restart, c.Action);
            Assert.IsTrue(c.DetectedInScript);
        }

        [TestMethod]
        public void RelativeScript_ResolvedAgainstWorkingDirectory()
        {
            Write("go.bat", "shutdown /r /t 0");
            Assert.AreEqual(PowerAction.Restart, Run("go.bat", "", _dir).Action);
        }

        [TestMethod]
        public void HarmlessScript_IsNotFlagged()
        {
            string bat = Write("clean.bat", "@echo off\r\nrem shutdown /r\r\n:: shutdown /r\r\ndel /q %TEMP%\\*.tmp\r\n");
            Classification c = Run(bat, "");
            Assert.AreEqual(PowerAction.None, c.Action);
            Assert.IsFalse(c.DetectedInScript);
        }

        [TestMethod]
        public void MissingOrLargeScript_IsNotFlagged()
        {
            Assert.AreEqual(PowerAction.None, Run(Path.Combine(_dir, "missing.bat"), "").Action);
            string big = Path.Combine(_dir, "big.bat");
            File.WriteAllText(big, "shutdown /r\r\n" + new string('x', (int)ScriptReader.MaxBytes));
            Assert.AreEqual(PowerAction.None, Run(big, "").Action);
        }

        [TestMethod]
        public void EncodedCommand_CallingScript()
        {
            string ps1 = Write("inner.ps1", "Stop-Computer -Force");
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("& '" + ps1 + "'"));
            Classification c = Run("powershell.exe", "-EncodedCommand " + encoded);
            Assert.AreEqual(PowerAction.Shutdown, c.Action);
            Assert.IsTrue(c.DetectedInScript);
        }
    }

    [TestClass]
    public class ForeignTaskAnalyzerTests
    {
        private static string TaskWith(string triggers, string actions, string principal = "<UserId>S-1-5-18</UserId>")
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<RegistrationInfo><Author>x</Author></RegistrationInfo><Triggers>" + triggers + "</Triggers>"
                + "<Principals><Principal id=\"Author\">" + principal + "</Principal></Principals><Settings><Enabled>true</Enabled></Settings>"
                + "<Actions Context=\"Author\">" + actions + "</Actions></Task>";
        }

        private const string Weekly = "<CalendarTrigger><StartBoundary>2026-10-01T04:00:00</StartBoundary><ScheduleByWeek><DaysOfWeek><Sunday /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger>";
        private const string Restart = "<Exec><Command>shutdown.exe</Command><Arguments>/r /t 0</Arguments></Exec>";

        [TestMethod]
        public void WeeklyRestart_IsAdoptable()
        {
            ForeignTaskAnalysis a = ForeignTaskAnalyzer.Analyze(TaskWith(Weekly, Restart), null);
            Assert.AreEqual(PowerAction.Restart, a.Action);
            Assert.IsTrue(a.CanAdopt);
            Assert.AreEqual("כל שבוע ביום ראשון בשעה 04:00", a.ScheduleText);
            Assert.AreEqual("S-1-5-18", a.UserId);
            Assert.AreEqual("shutdown.exe /r /t 0", a.CommandLine);
        }

        [TestMethod]
        public void GenericTriggerDescriptions()
        {
            Assert.AreEqual("בכל הפעלת מחשב", ForeignTaskAnalyzer.Analyze(TaskWith("<BootTrigger><Enabled>true</Enabled></BootTrigger>", Restart), null).ScheduleText);
            Assert.AreEqual("בכניסת משתמש", ForeignTaskAnalyzer.Analyze(TaskWith("<LogonTrigger />", Restart), null).ScheduleText);
            Assert.AreEqual("מספר טריגרים", ForeignTaskAnalyzer.Analyze(TaskWith(Weekly + "<BootTrigger />", Restart), null).ScheduleText);
            Assert.AreEqual("ללא טריגר", ForeignTaskAnalyzer.Analyze(TaskWith("", Restart), null).ScheduleText);
            Assert.AreEqual("כשהמחשב במצב סרק", ForeignTaskAnalyzer.Analyze(TaskWith("<IdleTrigger />", Restart), null).ScheduleText);
            Assert.AreEqual("בעת אירוע ביומן האירועים", ForeignTaskAnalyzer.Analyze(TaskWith("<EventTrigger><Subscription>x</Subscription></EventTrigger>", Restart), null).ScheduleText);
            string repeating = "<TimeTrigger><Repetition><Interval>PT1H</Interval></Repetition><StartBoundary>2026-10-01T04:00:00</StartBoundary></TimeTrigger>";
            Assert.AreEqual("חד־פעמי עם חזרה", ForeignTaskAnalyzer.Analyze(TaskWith(repeating, Restart), null).ScheduleText);
            Assert.IsFalse(ForeignTaskAnalyzer.Analyze(TaskWith("<BootTrigger />", Restart), null).CanAdopt);
        }

        [TestMethod]
        public void MultipleActions_RestartWins_AndGroupPrincipal()
        {
            string actions = "<Exec><Command>notepad.exe</Command></Exec><Exec><Command>shutdown</Command><Arguments>/s</Arguments></Exec>"
                + "<Exec><Command>powershell.exe</Command><Arguments>-c Restart-Computer</Arguments></Exec>";
            ForeignTaskAnalysis a = ForeignTaskAnalyzer.Analyze(TaskWith(Weekly, actions, "<GroupId>S-1-5-32-545</GroupId>"), null);
            Assert.AreEqual(PowerAction.Restart, a.Action);
            Assert.AreEqual("S-1-5-32-545", a.UserId);
            StringAssert.Contains(a.CommandLine, "notepad.exe");
        }

        [TestMethod]
        public void ComHandlerOnly_IsNone()
        {
            ForeignTaskAnalysis a = ForeignTaskAnalyzer.Analyze(TaskWith(Weekly, "<ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler>"), null);
            Assert.AreEqual(PowerAction.None, a.Action);
        }

        [TestMethod]
        public void UnknownOrBrokenXml_NeverThrows()
        {
            Assert.IsNull(ForeignTaskAnalyzer.Analyze("<not-xml", null));
            Assert.IsNull(ForeignTaskAnalyzer.Analyze("", null));
            ForeignTaskAnalysis odd = ForeignTaskAnalyzer.Analyze("<Task xmlns=\"urn:other\"><Weird /></Task>", null);
            Assert.AreEqual(PowerAction.None, odd.Action);
            Assert.AreEqual("ללא טריגר", odd.ScheduleText);
            Assert.AreEqual("לא ניתן לקרוא את הטריגר", TaskXml.DescribeTriggers("<<<"));
            Assert.IsNull(TaskXml.TryParseSchedule("<<<"));
        }
    }

    [TestClass]
    public class RunHistoryTests
    {
        private static readonly TimeSpan Three = new TimeSpan(3, 0, 0);

        [TestMethod]
        public void PreviousRun()
        {
            var now = new DateTime(2026, 10, 7, 10, 0, 0); // Wednesday
            Assert.AreEqual(new DateTime(2026, 10, 7, 3, 0, 0), RestartSchedule.Daily(Three).GetPreviousRun(now));
            Assert.AreEqual(new DateTime(2026, 10, 2, 3, 0, 0), RestartSchedule.Weekly(new[] { DayOfWeek.Friday }, Three).GetPreviousRun(now));
            Assert.AreEqual(new DateTime(2026, 9, 30, 3, 0, 0), RestartSchedule.MonthlyLastDay(Three).GetPreviousRun(now));
            Assert.AreEqual(new DateTime(2026, 8, 31, 3, 0, 0), RestartSchedule.MonthlyDay(31, Three).GetPreviousRun(now));
            Assert.AreEqual(new DateTime(2026, 9, 25, 3, 0, 0), RestartSchedule.MonthlyLastWeekday(DayOfWeek.Friday, Three).GetPreviousRun(now));
        }

        [TestMethod]
        public void Missed_WhenNoRunAfterExpectedOccurrence()
        {
            var schedule = RestartSchedule.Daily(Three);
            var now = new DateTime(2026, 10, 7, 10, 0, 0);
            var registered = new DateTime(2026, 10, 1, 12, 0, 0);
            Assert.IsTrue(RunHistory.IsMissed(schedule, registered, new DateTime(2026, 10, 6, 3, 0, 1), now), "yesterday ran, today did not");
            Assert.IsTrue(RunHistory.IsMissed(schedule, registered, null, now), "never ran");
            Assert.IsFalse(RunHistory.IsMissed(schedule, registered, new DateTime(2026, 10, 7, 3, 0, 2), now), "ran today");
        }

        [TestMethod]
        public void NotMissed_WithinGrace_BeforeRegistration_OrOneTime()
        {
            var schedule = RestartSchedule.Daily(Three);
            Assert.IsFalse(RunHistory.IsMissed(schedule, new DateTime(2026, 10, 1), new DateTime(2026, 10, 6, 3, 0, 0), new DateTime(2026, 10, 7, 3, 5, 0)), "today's run only 5 minutes late");
            Assert.IsFalse(RunHistory.IsMissed(schedule, new DateTime(2026, 10, 7, 9, 0, 0), null, new DateTime(2026, 10, 7, 10, 0, 0)), "registered after the occurrence");
            Assert.IsFalse(RunHistory.IsMissed(RestartSchedule.Once(new DateTime(2026, 10, 1), Three), null, null, new DateTime(2026, 10, 7)));
        }

        [TestMethod]
        public void OneTimeOutcome()
        {
            var once = RestartSchedule.Once(new DateTime(2026, 10, 7), Three);
            Assert.AreEqual(OneTimeResult.Pending, RunHistory.EvaluateOneTime(once, null, new DateTime(2026, 10, 7, 3, 5, 0)));
            // A recorded run is reported immediately, without the 10-minute grace.
            Assert.AreEqual(OneTimeResult.Executed, RunHistory.EvaluateOneTime(once, new DateTime(2026, 10, 7, 3, 0, 0), new DateTime(2026, 10, 7, 3, 0, 30)), "run exactly at trigger time");
            Assert.AreEqual(OneTimeResult.Executed, RunHistory.EvaluateOneTime(once, new DateTime(2026, 10, 7, 3, 1, 0), new DateTime(2026, 10, 7, 3, 2, 0)), "run one minute after trigger time");
            Assert.AreEqual("התזמון החד־פעמי בוצע ב־07/10/2026 בשעה 03:01", RunHistory.OneTimeText(OneTimeResult.Executed, new DateTime(2026, 10, 7, 3, 1, 0)));
            Assert.AreEqual(OneTimeResult.Pending, RunHistory.EvaluateOneTime(once, new DateTime(2026, 10, 7, 2, 59, 59), new DateTime(2026, 10, 7, 3, 5, 0)), "run before trigger time, still in grace");
            Assert.AreEqual(OneTimeResult.Missed, RunHistory.EvaluateOneTime(once, new DateTime(2026, 10, 7, 2, 59, 59), new DateTime(2026, 10, 7, 3, 10, 0)), "no run at or after trigger, grace over");
            Assert.AreEqual(OneTimeResult.Missed, RunHistory.EvaluateOneTime(once, null, new DateTime(2026, 10, 7, 3, 11, 0)));
            Assert.AreEqual(OneTimeResult.Executed, RunHistory.EvaluateOneTime(once, new DateTime(2026, 10, 7, 3, 0, 4), new DateTime(2026, 10, 7, 9, 0, 0)));
            Assert.AreEqual(OneTimeResult.Missed, RunHistory.EvaluateOneTime(once, new DateTime(2026, 10, 1, 3, 0, 0), new DateTime(2026, 10, 7, 9, 0, 0)), "older run");
            Assert.AreEqual(OneTimeResult.Pending, RunHistory.EvaluateOneTime(RestartSchedule.Daily(Three), null, new DateTime(2026, 10, 9)));
            Assert.AreEqual("התזמון החד־פעמי בוצע ב־07/10/2026 בשעה 03:00", RunHistory.OneTimeText(OneTimeResult.Executed, new DateTime(2026, 10, 7, 3, 0, 4)));
            Assert.AreEqual("התזמון החד־פעמי לא בוצע, המחשב היה כבוי", RunHistory.OneTimeText(OneTimeResult.Missed, null));
        }
    }

    [TestClass]
    public class BitLockerRuleTests
    {
        private static readonly int[] AllConversionStatuses = { 0, 1, 2, 3, 4, 5 }; // decrypted, encrypted, encrypting, decrypting, encryption paused, decryption paused

        // name, protector types, warns when the volume is not fully decrypted
        private static readonly object[][] Protectors =
        {
            new object[] { "TPM only", new[] { 1 }, false },
            new object[] { "TPM + recovery password", new[] { 1, 3 }, false },
            new object[] { "recovery password only", new[] { 3 }, false },
            new object[] { "TPM+PIN", new[] { 4 }, true },
            new object[] { "TPM+PIN + recovery password", new[] { 4, 3 }, true },
            new object[] { "passphrase only", new[] { 8 }, true },
            new object[] { "startup key", new[] { 2 }, true },
            new object[] { "TPM+startup key", new[] { 5 }, true },
            new object[] { "TPM+PIN+startup key", new[] { 6 }, true },
            new object[] { "public key / certificate", new[] { 7 }, false },
            new object[] { "no protectors", new int[0], false }
        };

        public static IEnumerable<object[]> Table()
        {
            foreach (int status in AllConversionStatuses)
                foreach (object[] p in Protectors)
                    yield return new object[] { status, p[0], p[1], status != 0 && (bool)p[2] };
        }

        [DataTestMethod]
        [DynamicData(nameof(Table), DynamicDataSourceType.Method)]
        public void Rule(int conversionStatus, string name, int[] protectorTypes, bool expected)
        {
            Assert.AreEqual(expected, BitLockerRule.StopsAtPreBoot(conversionStatus, protectorTypes), name + " / conversion status " + conversionStatus);
        }

        [TestMethod]
        public void NamedScenarios()
        {
            Assert.IsTrue(BitLockerRule.StopsAtPreBoot(2, new[] { 8 }), "passphrase while encrypting");
            // Protection status is not an input: a suspended TPM+PIN volume still asks for the PIN once resumed.
            Assert.IsTrue(BitLockerRule.StopsAtPreBoot(1, new[] { 4, 3 }), "TPM+PIN while suspended");
            Assert.IsFalse(BitLockerRule.StopsAtPreBoot(0, new[] { 4, 8, 2, 5, 6 }), "fully decrypted with stale protectors");
            Assert.IsFalse(BitLockerRule.StopsAtPreBoot(1, new int[0]), "no protectors");
            Assert.IsFalse(BitLockerRule.StopsAtPreBoot(1, null), "no protector list");
            Assert.IsFalse(BitLockerRule.StopsAtPreBoot(1, new[] { 1 }), "TPM only (Windows 11 device encryption)");
        }
    }

    [TestClass]
    public class SmallLogicTests
    {
        [TestMethod]
        public void LogRotation_AtLimit_RenamesToOld_ReplacingPrevious()
        {
            string dir = Path.Combine(Path.GetTempPath(), "srlog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string log = Path.Combine(dir, "ScheduledRestart.log");
                string old = LogRotation.OldPath(log);
                Assert.AreEqual(Path.Combine(dir, "ScheduledRestart.old"), old);
                File.WriteAllText(old, "previous old");
                File.WriteAllText(log, new string('a', 99));
                Assert.IsFalse(LogRotation.RotateIfNeeded(log, 100), "below limit");
                File.AppendAllText(log, "b");
                Assert.IsTrue(LogRotation.RotateIfNeeded(log, 100));
                Assert.IsFalse(File.Exists(log));
                Assert.AreEqual(100, new FileInfo(old).Length, "old replaced by the rotated log");
                Assert.IsFalse(LogRotation.RotateIfNeeded(log, 100), "missing log is fine");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void LicenseInfo_HebrewAndQuotes()
        {
            var info = LicenseInfo.FromMetadata(new[]
            {
                new KeyValuePair<string, string>("LicenseeName", "  חברת אלפא \"מחשבים\" בע\"מ  "),
                new KeyValuePair<string, string>("LicenseId", "IL-2026/001 \"A\"")
            });
            Assert.IsTrue(info.IsLicensed);
            Assert.AreEqual("חברת אלפא \"מחשבים\" בע\"מ", info.Name);
            Assert.AreEqual("IL-2026/001 \"A\"", info.Id);
            Assert.AreEqual("מורשה ל: חברת אלפא \"מחשבים\" בע\"מ", info.DisplayLine);
        }

        [TestMethod]
        public void LicenseInfo_Unlicensed()
        {
            var info = LicenseInfo.FromMetadata(new[] { new KeyValuePair<string, string>("LicenseeName", "") });
            Assert.IsFalse(info.IsLicensed);
            Assert.AreEqual("עותק הדגמה, לא מורשה להפצה", info.DisplayLine);
            Assert.IsFalse(LicenseInfo.FromMetadata(new KeyValuePair<string, string>[0]).IsLicensed);
        }

        [TestMethod]
        public void BackupNames_AreSafe()
        {
            var when = new DateTime(2026, 10, 7, 9, 5, 3);
            Assert.AreEqual("Nightly reboot-20261007-090503.xml", BackupNames.ForTask("Nightly reboot", when));
            Assert.AreEqual("a_b_c_d_e_f_g_h_i_-20261007-090503.xml", BackupNames.ForTask("a\\b/c:d*e?f\"g<h>i|", when));
            Assert.AreEqual("task-20261007-090503.xml", BackupNames.ForTask("  ", when));
            Assert.AreEqual(60 + "-20261007-090503.xml".Length, BackupNames.ForTask(new string('x', 200), when).Length);
        }
    }
}
