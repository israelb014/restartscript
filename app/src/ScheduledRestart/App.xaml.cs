using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using ScheduledRestart.Core;
using ScheduledRestart.Services;

namespace ScheduledRestart
{
    public partial class App : Application
    {
        private const string MutexName = @"Local\IBFix.ScheduledRestart.SingleInstance";
        private Mutex _mutex;

        internal static RestartLog Log { get; private set; }
        internal static ScheduleManager Manager { get; private set; }
        internal static ForeignTaskService Foreign { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                NativeMethods.ActivateOtherInstance();
                Shutdown();
                return;
            }

            var hebrew = new CultureInfo("he-IL");
            Thread.CurrentThread.CurrentCulture = hebrew;
            Thread.CurrentThread.CurrentUICulture = hebrew;
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(hebrew.IetfLanguageTag)));

            Log = new RestartLog(AppConstants.LogFile, AppConstants.EventSource);
            Manager = ScheduleManager.CreateDefault(Log);
            Foreign = ForeignTaskService.CreateDefault(Log);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrash(args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
            new MainWindow().Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
                _mutex.Dispose();
            }
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash(e.Exception);
            e.Handled = true;
            try
            {
                MessageDialog.Show(MainWindow, "אירעה שגיאה בלתי צפויה. הפרטים נרשמו בלוג.\n\n" + e.Exception.Message, MessageKind.Error);
            }
            catch (Exception)
            {
                ShowFallbackError(e.Exception);
            }
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject));
            LogCrash(ex);
            ShowFallbackError(ex);
        }

        private static void LogCrash(Exception ex)
        {
            if (Log != null)
                Log.Write("שגיאה בלתי צפויה: " + ex.GetType().Name + ": " + ex.Message, EventLogEntryType.Error, AppConstants.EventFailure);
            Trace.WriteLine(ex.ToString());
        }

        private static void ShowFallbackError(Exception ex)
        {
            MessageBox.Show("אירעה שגיאה בלתי צפויה. הפרטים נרשמו בלוג.\n\n" + ex.Message, "תזמון הפעלה מחדש",
                MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
    }
}
