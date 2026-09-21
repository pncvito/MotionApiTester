using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

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

        /// <summary>
        /// 往实时日志面板追加一行。
        /// 走 LogTextBuffer（无锁并发队列 + UI 定时器刷出），因此允许从任意线程调用 ——
        /// AssemblyResolve 就完全可能发生在后台线程上。
        /// </summary>
        internal void AppendLog(string message) => _logBuffer.Write(message);

        /// <summary>
        /// 导出当前日志到文件。
        /// 格式跟随设置里的「导出格式」—— 以前这个设置项存了却没人读，永远只写 .txt。
        /// </summary>
        private void ExportLog()
        {
            var asJson = string.Equals(_settingsService.Settings.ExportFormat, "json",
                                       StringComparison.OrdinalIgnoreCase);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = asJson ? "JSON 文件|*.json|所有文件|*.*" : "文本文件|*.txt|所有文件|*.*",
                FileName = $"MotionApiTester-{DateTime.Now:yyyyMMdd-HHmmss}.{(asJson ? "json" : "txt")}"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (asJson) WriteJsonExport(dlg.FileName);
                else File.WriteAllText(dlg.FileName, LogText);

                StatusText = $"✅ 日志已导出: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"❌ 导出失败: {ex.Message}";
            }
        }

        /// <summary>
        /// JSON 导出：日志行 + 调用历史一起给出。
        /// 只把 LogText 塞成一个 JSON 字符串没有意义 —— 真正结构化的是调用历史
        /// （成功与否、耗时、参数），排查时要的就是它。
        /// </summary>
        private void WriteJsonExport(string path)
        {
            var payload = new
            {
                exportedAt = DateTime.Now,
                loadedPath = CurrentLoadedPath,
                machineType = MachineTypeText,
                logLines = (LogText ?? "").Split('\n'),
                history = _historyItems.ToList()
            };

            // 不转义非 ASCII，否则导出的中文全是 \uXXXX，人没法看
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
    }
}
