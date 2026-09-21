using System;
using System.IO;

namespace MotionApiTester.ViewModels
{
    /// <summary>日志缓冲刷新与导出。</summary>
    public partial class MainViewModel
    {
        /// <summary>
        /// 把 LogTextBuffer 里的待输出行合并进 LogText（由 100ms 定时器在 UI 线程驱动）。
        /// 内容无变化时 Flush 返回原字符串，SetProperty 会自行跳过通知。
        /// </summary>
        private void FlushLogQueue() => LogText = _logBuffer.Flush(LogText, _maxLogLines);

        /// <summary>导出当前日志到文件</summary>
        private void ExportLog()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "文本文件|*.txt|所有文件|*.*",
                FileName = $"MotionApiTester-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                File.WriteAllText(dlg.FileName, LogText);
                StatusText = $"✅ 日志已导出: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"❌ 导出失败: {ex.Message}";
            }
        }
    }
}
