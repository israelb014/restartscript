using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ScheduledRestart.Services
{
    /// <summary>State of a registered task as reported by Task Scheduler.</summary>
    internal sealed class RegisteredTaskState
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public string Xml { get; set; }
        public bool Enabled { get; set; }
        public DateTime? NextRun { get; set; }
        public DateTime? LastRun { get; set; }
        public int LastResult { get; set; }
    }

    internal sealed class TaskEnumeration
    {
        public TaskEnumeration()
        {
            Tasks = new List<RegisteredTaskState>();
        }

        public List<RegisteredTaskState> Tasks { get; private set; }

        /// <summary>Tasks or folders that could not be read (usually permissions).</summary>
        public int Unreadable { get; set; }
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

        /// <summary>Full path of the task this client manages, e.g. \ScheduledRestart\ScheduledRestart.</summary>
        public string TaskPath
        {
            get { return FolderPath + "\\" + TaskName; }
        }

        /// <summary>Returns the task state, or null when the task does not exist.</summary>
        public RegisteredTaskState GetState()
        {
            return GetStateAt(TaskPath);
        }

        /// <summary>Returns the task XML, or null when the task does not exist.</summary>
        public string GetTaskXml()
        {
            RegisteredTaskState state = GetStateAt(TaskPath);
            return state == null ? null : state.Xml;
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
            SetEnabledAt(TaskPath, enabled);
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

        // ---- Any task, by full path ----

        /// <summary>State of the task at <paramref name="path"/>, or null when it does not exist.</summary>
        public static RegisteredTaskState GetStateAt(string path)
        {
            return WithTaskAt(path, ReadState);
        }

        public static void SetEnabledAt(string path, bool enabled)
        {
            bool found = WithTaskAt(path, task =>
            {
                task.Enabled = enabled;
                return true;
            });
            if (!found) throw new InvalidOperationException("המשימה לא נמצאה.");
        }

        /// <summary>Deletes the task at <paramref name="path"/>; its folder is left in place.</summary>
        public static void DeleteAt(string path)
        {
            string parent, name;
            SplitPath(path, out parent, out name);
            dynamic service = null, folder = null;
            try
            {
                service = Connect();
                folder = service.GetFolder(parent);
                folder.DeleteTask(name, 0);
            }
            finally
            {
                Release(folder, service);
            }
        }

        /// <summary>
        /// Every task in <paramref name="rootFolder"/> and below, including hidden ones, except folders
        /// for which <paramref name="skipFolder"/> returns true. Unreadable tasks and folders are counted.
        /// </summary>
        public static TaskEnumeration Enumerate(string rootFolder, Func<string, bool> skipFolder)
        {
            var result = new TaskEnumeration();
            dynamic service = null, root = null;
            try
            {
                service = Connect();
                try
                {
                    root = service.GetFolder(rootFolder);
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    return result;
                }
                Walk(root, skipFolder, result, 0);
            }
            finally
            {
                Release(root, service);
            }
            return result;
        }

        private static void Walk(dynamic folder, Func<string, bool> skipFolder, TaskEnumeration result, int depth)
        {
            if (depth > 32) return;
            dynamic tasks = null, folders = null;
            try
            {
                try
                {
                    tasks = folder.GetTasks(TaskEnumHidden);
                    int count = (int)tasks.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic task = null;
                        try
                        {
                            task = tasks.Item(i);
                            result.Tasks.Add(ReadState(task));
                        }
                        catch (Exception ex) when (IsReadFailure(ex))
                        {
                            result.Unreadable++;
                        }
                        finally
                        {
                            Release(task);
                        }
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    result.Unreadable++;
                }

                try
                {
                    folders = folder.GetFolders(0);
                    int count = (int)folders.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic sub = null;
                        try
                        {
                            sub = folders.Item(i);
                            if (!skipFolder((string)sub.Path)) Walk(sub, skipFolder, result, depth + 1);
                        }
                        catch (Exception ex) when (IsReadFailure(ex))
                        {
                            result.Unreadable++;
                        }
                        finally
                        {
                            Release(sub);
                        }
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    result.Unreadable++;
                }
            }
            finally
            {
                Release(folders, tasks);
            }
        }

        private static RegisteredTaskState ReadState(dynamic task)
        {
            var state = new RegisteredTaskState
            {
                Path = (string)task.Path,
                Name = (string)task.Name,
                Xml = (string)task.Xml,
                Enabled = (bool)task.Enabled,
                LastResult = (int)task.LastTaskResult
            };
            DateTime next = (DateTime)task.NextRunTime;
            DateTime last = (DateTime)task.LastRunTime;
            if (next.Year >= 2000) state.NextRun = next;
            if (last.Year >= 2000) state.LastRun = last;
            return state;
        }

        private static T WithTaskAt<T>(string path, Func<dynamic, T> action)
        {
            string parent, name;
            SplitPath(path, out parent, out name);
            dynamic service = null, folder = null, task = null;
            try
            {
                service = Connect();
                try
                {
                    folder = service.GetFolder(parent);
                    task = folder.GetTask(name);
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

        private static void SplitPath(string path, out string parent, out string name)
        {
            int slash = path.LastIndexOf('\\');
            parent = slash <= 0 ? "\\" : path.Substring(0, slash);
            name = path.Substring(slash + 1);
        }

        private static dynamic Connect()
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service", true);
            dynamic service = Activator.CreateInstance(type);
            service.Connect();
            return service;
        }

        /// <summary>Any failure reading one task or folder: it is skipped and counted, never fatal.</summary>
        private static bool IsReadFailure(Exception ex)
        {
            return !(ex is OutOfMemoryException || ex is StackOverflowException);
        }

        internal static bool IsNotFound(Exception ex)
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
