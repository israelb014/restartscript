using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Text;
using ScheduledRestart.Core;

namespace ScheduledRestart.Services
{
    /// <summary>A restart or shutdown task that was not created by this app.</summary>
    internal sealed class ForeignTask
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public PowerAction Action { get; set; }
        public bool Enabled { get; set; }
        public string Account { get; set; }
        public string CommandLine { get; set; }
        public string ScheduleText { get; set; }
        public RestartSchedule Schedule { get; set; }
        public DateTime? NextRun { get; set; }
        public DateTime? LastRun { get; set; }
        public bool DetectedInScript { get; set; }

        public bool CanAdopt
        {
            get { return Action == PowerAction.Restart && Schedule != null; }
        }

        public string KindText
        {
            get { return Action == PowerAction.Restart ? "הפעלה מחדש" : "כיבוי"; }
        }
    }

    internal sealed class ForeignScanResult
    {
        public ForeignScanResult(List<ForeignTask> tasks, int unreadable)
        {
            Tasks = tasks;
            Unreadable = unreadable;
        }

        public List<ForeignTask> Tasks { get; private set; }
        public int Unreadable { get; private set; }
    }

    internal sealed class AdoptResult
    {
        public AdoptResult(bool success, string error)
        {
            Success = success;
            Error = error;
        }

        public bool Success { get; private set; }
        public string Error { get; private set; }
    }

    /// <summary>Finds restart/shutdown tasks created by other tools and lets the technician manage them.</summary>
    internal sealed class ForeignTaskService
    {
        private readonly string _root;
        private readonly Func<string, bool> _skipFolder;
        private readonly string _ownTaskPath;
        private readonly RestartLog _log;
        private readonly string _backupDirectory;

        public ForeignTaskService(string rootFolder, Func<string, bool> skipFolder, string ownTaskPath, RestartLog log, string backupDirectory)
        {
            _root = rootFolder;
            _skipFolder = skipFolder;
            _ownTaskPath = ownTaskPath;
            _log = log;
            _backupDirectory = backupDirectory;
        }

        /// <summary>Hook for integration tests: throwing here simulates a failure while disabling the original task.</summary>
        internal Action BeforeDisableOriginalForTesting { get; set; }

        public static ForeignTaskService CreateDefault(RestartLog log)
        {
            return new ForeignTaskService("\\", IsSkippedFolder,
                "\\" + AppConstants.TaskFolderName + "\\" + AppConstants.TaskName, log, AppConstants.BackupsDirectory);
        }

        /// <summary>Skips \Microsoft\... (Windows' own tasks) and the app's integration test folder.</summary>
        public static bool IsSkippedFolder(string path)
        {
            return IsUnder(path, "\\Microsoft") || IsUnder(path, "\\" + AppConstants.TestTaskFolderName);
        }

        private static bool IsUnder(string path, string folder)
        {
            return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public ForeignScanResult Scan()
        {
            TaskEnumeration all = TaskSchedulerClient.Enumerate(_root, _skipFolder);
            var found = new List<ForeignTask>();
            int unreadable = all.Unreadable;
            foreach (RegisteredTaskState state in all.Tasks)
            {
                if (string.Equals(state.Path, _ownTaskPath, StringComparison.OrdinalIgnoreCase)) continue;
                ForeignTaskAnalysis analysis = ForeignTaskAnalyzer.Analyze(state.Xml, ScriptReader.TryRead);
                if (analysis == null)
                {
                    unreadable++;
                    continue;
                }
                if (analysis.Action == PowerAction.None) continue;
                found.Add(new ForeignTask
                {
                    Path = state.Path,
                    Name = state.Name,
                    Action = analysis.Action,
                    Enabled = state.Enabled,
                    Account = AccountName(analysis.UserId),
                    CommandLine = analysis.CommandLine,
                    ScheduleText = analysis.ScheduleText,
                    Schedule = analysis.Schedule,
                    NextRun = state.NextRun,
                    LastRun = state.LastRun,
                    DetectedInScript = analysis.DetectedInScript
                });
            }
            found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return new ForeignScanResult(found, unreadable);
        }

        private static string AccountName(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return "לא ידוע";
            if (!userId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return userId;
            try
            {
                return new SecurityIdentifier(userId).Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException || ex is ArgumentException || ex is SystemException)
            {
                return userId;
            }
        }

        public void SetEnabled(ForeignTask task, bool enabled)
        {
            TaskSchedulerClient.SetEnabledAt(task.Path, enabled);
            RegisteredTaskState state = TaskSchedulerClient.GetStateAt(task.Path);
            if (state == null || state.Enabled != enabled)
                throw new InvalidOperationException(enabled ? "הפעלת התזמון לא אומתה." : "השבתת התזמון לא אומתה.");
            _log.Write(string.Format("תזמון נוסף {0}: {1}", enabled ? "הופעל" : "הושבת", task.Path), EventLogEntryType.Information, AppConstants.EventForeignTask);
        }

        /// <summary>Saves the task XML to the backups folder, deletes the task and verifies it is gone. Returns the backup path.</summary>
        public string Delete(ForeignTask task)
        {
            RegisteredTaskState state = TaskSchedulerClient.GetStateAt(task.Path);
            if (state == null) throw new InvalidOperationException("המשימה לא נמצאה.");
            Directory.CreateDirectory(_backupDirectory);
            string backup = System.IO.Path.Combine(_backupDirectory, BackupNames.ForTask(task.Name, DateTime.Now));
            File.WriteAllText(backup, state.Xml, Encoding.Unicode);

            TaskSchedulerClient.DeleteAt(task.Path);
            if (TaskSchedulerClient.GetStateAt(task.Path) != null) throw new InvalidOperationException("המשימה עדיין קיימת לאחר המחיקה.");
            _log.Write(string.Format("תזמון נוסף נמחק: {0}. גיבוי: {1}", task.Path, backup), EventLogEntryType.Information, AppConstants.EventForeignTask);
            return backup;
        }

        /// <summary>
        /// Creates the app's task with the same schedule, verifies it, then disables (does not delete) the
        /// original. Any failure restores both tasks to their previous state.
        /// </summary>
        public AdoptResult Adopt(ForeignTask task, ScheduleManager app)
        {
            if (!task.CanAdopt) return new AdoptResult(false, "לא ניתן להעביר תזמון זה לניהול התוכנה.");
            RegisteredTaskState original = TaskSchedulerClient.GetStateAt(task.Path);
            if (original == null) return new AdoptResult(false, "המשימה המקורית לא נמצאה.");
            string previousAppXml = app.Client.GetTaskXml();

            SaveResult saved = app.Save(task.Schedule);
            if (saved.Outcome != SaveOutcome.Saved)
                return new AdoptResult(false, saved.Error);

            try
            {
                if (BeforeDisableOriginalForTesting != null) BeforeDisableOriginalForTesting();
                TaskSchedulerClient.SetEnabledAt(task.Path, false);
                RegisteredTaskState after = TaskSchedulerClient.GetStateAt(task.Path);
                if (after == null || after.Enabled) throw new InvalidOperationException("השבתת התזמון המקורי לא אומתה.");
                _log.Write(string.Format("התזמון {0} הועבר לניהול התוכנה. התזמון המקורי הושבת.", task.Path),
                    EventLogEntryType.Information, AppConstants.EventForeignTask);
                return new AdoptResult(true, null);
            }
            catch (Exception ex)
            {
                string error = ex.Message;
                try
                {
                    if (previousAppXml == null) app.Client.Delete();
                    else app.Client.Register(previousAppXml);
                }
                catch (Exception restoreError)
                {
                    error += " | " + restoreError.Message;
                }
                try
                {
                    if (original.Enabled) TaskSchedulerClient.SetEnabledAt(task.Path, true);
                }
                catch (Exception restoreError)
                {
                    error += " | " + restoreError.Message;
                }
                _log.Write(string.Format("העברת התזמון {0} לניהול התוכנה נכשלה והמצב הקודם שוחזר: {1}", task.Path, error),
                    EventLogEntryType.Warning, AppConstants.EventRollback);
                return new AdoptResult(false, error);
            }
        }
    }

    /// <summary>"Remove everything": the app's task, its data folder and its event source. Never touches other tasks.</summary>
    internal static class Uninstaller
    {
        public static List<string> RemoveAll(TaskSchedulerClient client, string dataDirectory, string eventSource)
        {
            var problems = new List<string>();
            try
            {
                client.Delete();
                if (client.GetTaskXml() != null) problems.Add("המשימה של התוכנה לא נמחקה.");
            }
            catch (Exception ex)
            {
                problems.Add("מחיקת המשימה נכשלה: " + ex.Message);
            }
            try
            {
                if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
            }
            catch (Exception ex)
            {
                problems.Add("מחיקת התיקייה " + dataDirectory + " נכשלה: " + ex.Message);
            }
            if (eventSource != null)
            {
                try
                {
                    if (EventLog.SourceExists(eventSource)) EventLog.DeleteEventSource(eventSource);
                }
                catch (Exception ex)
                {
                    problems.Add("הסרת מקור יומן האירועים נכשלה: " + ex.Message);
                }
            }
            return problems;
        }
    }

    /// <summary>Reads the system drive's BitLocker state over WMI and applies <see cref="BitLockerRule"/>.</summary>
    internal static class BitLocker
    {
        private const string Namespace = @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption";

        /// <summary>
        /// True when a restart would stop at a pre-boot prompt. Never throws: when the namespace is missing,
        /// access is denied or any query fails it returns false and describes the failure in <paramref name="error"/>.
        /// </summary>
        public static bool ShouldWarn(out string error)
        {
            error = null;
            try
            {
                int conversionStatus;
                int[] protectorTypes;
                if (!TryReadSystemVolume(out conversionStatus, out protectorTypes)) return false;
                return BitLockerRule.StopsAtPreBoot(conversionStatus, protectorTypes);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <returns>False when the system drive is not an encryptable volume (nothing to check).</returns>
        private static bool TryReadSystemVolume(out int conversionStatus, out int[] protectorTypes)
        {
            conversionStatus = BitLockerRule.FullyDecrypted;
            protectorTypes = new int[0];
            string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var scope = new ManagementScope(Namespace);
            scope.Connect();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume")))
            using (ManagementObjectCollection volumes = searcher.Get())
            {
                foreach (ManagementObject volume in volumes)
                {
                    using (volume)
                    {
                        if (!string.Equals(volume["DriveLetter"] as string, drive, StringComparison.OrdinalIgnoreCase)) continue;

                        using (ManagementBaseObject status = Call(volume, "GetConversionStatus", null))
                            conversionStatus = Convert.ToInt32(status["ConversionStatus"]);

                        var types = new List<int>();
                        string[] ids;
                        using (ManagementBaseObject input = volume.GetMethodParameters("GetKeyProtectors"))
                        {
                            input["KeyProtectorType"] = 0u; // all types
                            using (ManagementBaseObject result = Call(volume, "GetKeyProtectors", input))
                                ids = result["VolumeKeyProtectorID"] as string[] ?? new string[0];
                        }
                        foreach (string id in ids)
                        {
                            using (ManagementBaseObject input = volume.GetMethodParameters("GetKeyProtectorType"))
                            {
                                input["VolumeKeyProtectorID"] = id;
                                using (ManagementBaseObject result = Call(volume, "GetKeyProtectorType", input))
                                    types.Add(Convert.ToInt32(result["KeyProtectorType"]));
                            }
                        }
                        protectorTypes = types.ToArray();
                        return true;
                    }
                }
            }
            return false;
        }

        private static ManagementBaseObject Call(ManagementObject volume, string method, ManagementBaseObject input)
        {
            ManagementBaseObject result = volume.InvokeMethod(method, input, null);
            uint code = Convert.ToUInt32(result["ReturnValue"]);
            if (code != 0)
            {
                result.Dispose();
                throw new InvalidOperationException(string.Format("{0} returned 0x{1:X8}", method, code));
            }
            return result;
        }
    }
}
