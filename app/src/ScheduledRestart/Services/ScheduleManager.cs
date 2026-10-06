using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ScheduledRestart.Core;

namespace ScheduledRestart.Services
{
    internal enum TaskPresence
    {
        None,
        Active,
        Paused,
        Unrecognized
    }

    /// <summary>Everything the status card needs, read in one go.</summary>
    internal sealed class ScheduleStatus
    {
        public TaskPresence Presence { get; set; }
        public RestartSchedule Schedule { get; set; }
        public DateTime? NextRun { get; set; }
        public DateTime? LastRun { get; set; }
        public int LastResult { get; set; }
    }

    internal enum SaveOutcome
    {
        Saved,
        FailedRestored,
        Failed
    }

    internal sealed class SaveResult
    {
        public SaveResult(SaveOutcome outcome, string error)
        {
            Outcome = outcome;
            Error = error;
        }

        public SaveOutcome Outcome { get; private set; }
        public string Error { get; private set; }
    }

    /// <summary>Create / replace (with backup, verification and rollback), pause, resume and delete.</summary>
    internal sealed class ScheduleManager
    {
        private readonly TaskSchedulerClient _client;
        private readonly RestartLog _log;
        private readonly string _backupPath;

        public ScheduleManager(TaskSchedulerClient client, RestartLog log, string backupPath)
        {
            _client = client;
            _log = log;
            _backupPath = backupPath;
        }

        public static ScheduleManager CreateDefault(RestartLog log)
        {
            return new ScheduleManager(new TaskSchedulerClient(AppConstants.TaskFolderName, AppConstants.TaskName), log, AppConstants.BackupFile);
        }

        public ScheduleStatus GetStatus()
        {
            RegisteredTaskState state = _client.GetState();
            if (state == null) return new ScheduleStatus { Presence = TaskPresence.None };

            RestartSchedule schedule = null;
            try { schedule = TaskXml.Parse(state.Xml).Schedule; }
            catch (System.Xml.XmlException) { }

            TaskPresence presence = schedule == null ? TaskPresence.Unrecognized
                : state.Enabled ? TaskPresence.Active : TaskPresence.Paused;
            return new ScheduleStatus
            {
                Presence = presence,
                Schedule = schedule,
                NextRun = state.NextRun,
                LastRun = state.LastRun,
                LastResult = state.LastResult
            };
        }

        public SaveResult Save(RestartSchedule schedule)
        {
            string previousXml = _client.GetTaskXml();
            if (previousXml != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_backupPath));
                    File.WriteAllText(_backupPath, previousXml, Encoding.Unicode);
                }
                catch (Exception ex)
                {
                    _log.Write("גיבוי התזמון הקודם לקובץ נכשל (הגיבוי נשמר בזיכרון): " + ex.Message);
                }
            }

            string description = string.Format(CultureInfo.InvariantCulture,
                "נוצר על ידי {0} בתאריך {1:dd/MM/yyyy HH:mm} באמצעות Scheduled Restart. {2}. {3}.",
                RestartLog.CurrentUser, DateTime.Now, HebrewText.Describe(schedule), HebrewText.WarningSummary(schedule.WarningMinutes));
            string xml = TaskXml.Build(schedule, RestartLog.CurrentUser, description, DateTime.Today);
            return Apply(xml, schedule, previousXml);
        }

        /// <summary>
        /// Registers <paramref name="xml"/>, verifies the result against <paramref name="expected"/> and
        /// restores <paramref name="previousXml"/> on any failure. Separate from Save so tests can force a mismatch.
        /// </summary>
        internal SaveResult Apply(string xml, RestartSchedule expected, string previousXml)
        {
            string problem;
            try
            {
                _client.Register(xml);
                problem = Verify(expected);
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            string requested = HebrewText.Describe(expected);
            if (problem == null)
            {
                string verb = previousXml == null ? "נוצר תזמון" : "התזמון הוחלף";
                _log.Write(string.Format("{0}: {1}. {2}.", verb, requested, HebrewText.WarningSummary(expected.WarningMinutes)),
                    EventLogEntryType.Information, AppConstants.EventCreated);
                return new SaveResult(SaveOutcome.Saved, null);
            }

            _log.Write(string.Format("שמירת התזמון נכשלה ({0}): {1}", requested, problem), EventLogEntryType.Error, AppConstants.EventFailure);

            if (previousXml == null)
            {
                try { _client.Delete(); }
                catch (Exception ex) { problem += " | " + ex.Message; }
                return new SaveResult(SaveOutcome.Failed, problem);
            }

            try
            {
                _client.Register(previousXml);
                if (_client.GetTaskXml() == null) throw new InvalidOperationException("התזמון הקודם לא נמצא לאחר השחזור.");
                _log.Write("התזמון הקודם שוחזר מהגיבוי.", EventLogEntryType.Warning, AppConstants.EventRollback);
                return new SaveResult(SaveOutcome.FailedRestored, problem);
            }
            catch (Exception ex)
            {
                _log.Write("שחזור התזמון הקודם נכשל: " + ex.Message + ". גיבוי: " + _backupPath, EventLogEntryType.Error, AppConstants.EventFailure);
                return new SaveResult(SaveOutcome.Failed, problem + " | " + ex.Message);
            }
        }

        /// <summary>Reads the task back and returns a Hebrew description of the first mismatch, or null.</summary>
        private string Verify(RestartSchedule expected)
        {
            RegisteredTaskState state = _client.GetState();
            if (state == null) return "המשימה לא נמצאה לאחר השמירה.";
            TaskXmlInfo info = TaskXml.Parse(state.Xml);
            if (info.Schedule == null || !info.Schedule.Equals(expected)) return "התזמון שנשמר אינו תואם לתזמון שנבחר.";
            if (info.Command == null || !info.Command.EndsWith("shutdown.exe", StringComparison.OrdinalIgnoreCase)
                || info.Arguments != TaskXml.BuildShutdownArguments(expected.WarningMinutes))
                return "פעולת המשימה אינה תואמת.";
            if (info.UserId != AppConstants.SystemSid || info.RunLevel != "HighestAvailable")
                return "חשבון ההרצה של המשימה אינו SYSTEM עם הרשאות מלאות.";
            if (!state.Enabled || !info.Enabled) return "המשימה אינה פעילה.";
            return null;
        }

        public void SetEnabled(bool enabled)
        {
            _client.SetEnabled(enabled);
            RegisteredTaskState state = _client.GetState();
            if (state == null || state.Enabled != enabled)
                throw new InvalidOperationException(enabled ? "חידוש התזמון לא אומת." : "השהיית התזמון לא אומתה.");
            if (enabled)
                _log.Write("התזמון חודש.", EventLogEntryType.Information, AppConstants.EventResumed);
            else
                _log.Write("התזמון הושהה.", EventLogEntryType.Information, AppConstants.EventPaused);
        }

        public void Delete()
        {
            ScheduleStatus before = GetStatus();
            _client.Delete();
            if (_client.GetTaskXml() != null) throw new InvalidOperationException("המשימה עדיין קיימת לאחר המחיקה.");
            string what = before.Schedule != null ? HebrewText.Describe(before.Schedule) : "תזמון בפורמט לא מוכר";
            _log.Write("התזמון בוטל: " + what + ".", EventLogEntryType.Information, AppConstants.EventDeleted);
        }

        public void LogFailure(string action, Exception ex)
        {
            _log.Write(string.Format("{0} נכשל: {1}", action, ex.Message), EventLogEntryType.Error, AppConstants.EventFailure);
        }
    }
}
