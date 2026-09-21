using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MotionApiTester
{
    public partial class App : Application
    {
        public App()
        {
            // 捕获全局未处理异常,写日志
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                WriteCrashLog(e.ExceptionObject as Exception);
            };
            DispatcherUnhandledException += (s, e) =>
            {
                WriteCrashLog(e.Exception);
                e.Handled = true; // 不要让应用挂掉
            };
        }

        private static void WriteCrashLog(Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "MotionApiTester", "crash.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n\n");
            }
            catch { }
        }
    }
}