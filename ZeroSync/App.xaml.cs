using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ZeroSync
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Bắt tất cả unhandled exception để ghi log thay vì silent crash
            Current.DispatcherUnhandledException += OnDispatcherUnhandled;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        }

        private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogCrash("UI Thread", e.Exception);
            e.Handled = true; // Không tắt app, hiện dialog
            MessageBox.Show(
                "Lỗi không xử lý được:\n" + e.Exception.Message +
                "\n\nXem logs/crash.log để biết thêm chi tiết.",
                "ZeroSync — Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            LogCrash("AppDomain", e.ExceptionObject as Exception);
        }

        private static void LogCrash(string source, Exception ex)
        {
            try
            {
                var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "crash.log"),
                    $"\r\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ===\r\n" +
                    (ex?.ToString() ?? "null") + "\r\n");
            }
            catch { }
        }
    }
}
