using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ScheduledRestart.Services
{
    /// <summary>State of the registered task as reported by Task Scheduler.</summary>
    internal sealed class RegisteredTaskState
    {
        public string Xml { get; set; }
        public bool Enabled { get; set; }
        public DateTime? NextRun { get; set; }
        public DateTime? LastRun { get; set; }
        public int LastResult { get; set; }
    }

    /// <summary>Minimal late-bound wrapper around the Task Scheduler 2.0 COM API (Schedule.Service).</summary>
    internal sealed class TaskSchedulerClient
    {
        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonServiceAccount = 5;
        private const int TaskEnumHidden = 1;

        public TaskSchedulerClient(string folderName, string taskName)
        {
            FolderName = folderName;
            TaskName = taskName;
        }

        public string FolderName { get; private set; }
        public string TaskName { get; private set; }

        private string FolderPath
        {
            get { return "\\" + FolderName; }
        }

        /// <summary>Returns the task state, or null when the task does not exist.</summary>
        public RegisteredTaskState GetState()
        {
            return WithTask(task =>
            {
                var state = new RegisteredTaskState
                {
                    Xml = (string)task.Xml,
                    Enabled = (bool)task.Enabled,
                    LastResult = (int)task.LastTaskResult
                };
                DateTime next = (DateTime)task.NextRunTime;
                DateTime last = (DateTime)task.LastRunTime;
                if (next.Year >= 2000) state.NextRun = next;
                if (last.Year >= 2000) state.LastRun = last;
                return state;
            });
        }

        /// <summary>Returns the task XML, or null when the task does not exist.</summary>
        public string GetTaskXml()
        {
            return WithTask(task => (string)task.Xml);
        }

        /// <summary>Creates or replaces the task from XML, running as SYSTEM.</summary>
        public void Register(string xml)
        {
            dynamic service = null, root = null, folder = null, task = null;
            try
            {
                service = Connect();
                try
                {
                    folder = service.GetFolder(FolderPath);
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    root = service.GetFolder("\\");
                    folder = root.CreateFolder(FolderName);
                }
                string account = new SecurityIdentifier(Core.AppConstants.SystemSid).Translate(typeof(NTAccount)).Value;
                task = folder.RegisterTask(TaskName, xml, TaskCreateOrUpdate, account, null, TaskLogonServiceAccount);
            }
            finally
            {
                Release(task, folder, root, service);
            }
        }

        /// <summary>Enables or disables (pauses) the task.</summary>
        public void SetEnabled(bool enabled)
        {
            bool found = WithTask(task =>
            {
                task.Enabled = enabled;
                return true;
            });
            if (!found) throw new InvalidOperationException("המשימה לא נמצאה.");
        }

        /// <summary>Deletes the task and removes the task folder when it is empty.</summary>
        public void Delete()
        {
            dynamic service = null, root = null, folder = null, tasks = null, folders = null;
            try
            {
                service = Connect();
                try
                {
                    folder = service.GetFolder(FolderPath);
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    return;
                }
                try
                {
                    folder.DeleteTask(TaskName, 0);
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                }
                tasks = folder.GetTasks(TaskEnumHidden);
                folders = folder.GetFolders(0);
                if ((int)tasks.Count == 0 && (int)folders.Count == 0)
                {
                    root = service.GetFolder("\\");
                    root.DeleteFolder(FolderName, 0);
                }
            }
            finally
            {
                Release(folders, tasks, folder, root, service);
            }
        }

        private T WithTask<T>(Func<dynamic, T> action)
        {
            dynamic service = null, folder = null, task = null;
            try
            {
                service = Connect();
                try
                {
                    folder = service.GetFolder(FolderPath);
                    task = folder.GetTask(TaskName);
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    return default(T);
                }
                return action(task);
            }
            finally
            {
                Release(task, folder, service);
            }
        }

        private static dynamic Connect()
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service", true);
            dynamic service = Activator.CreateInstance(type);
            service.Connect();
            return service;
        }

        private static bool IsNotFound(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                // 0x80070002 file not found, 0x80070003 path not found
                if (e.HResult == unchecked((int)0x80070002) || e.HResult == unchecked((int)0x80070003)) return true;
            }
            return false;
        }

        private static void Release(params object[] comObjects)
        {
            foreach (object o in comObjects)
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
            }
        }
    }
}
