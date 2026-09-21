using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 设备调用日志缓冲。
    ///
    /// 生产端在后台线程（ApiInvoker、设备 logger 回调），消费端在 UI 线程，
    /// 因此入队用无锁并发队列，由 UI 定时器统一 Flush 成文本 ——
    /// 避免后台线程直接改绑定属性触发跨线程 INPC 异常。
    /// </summary>
    public class LogTextBuffer
    {
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();

        /// <summary>线程安全：写入一行（自动补时间戳）</summary>
        public void Write(string message) =>
            _pending.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

        /// <summary>是否还有未输出的内容</summary>
        public bool HasPending => !_pending.IsEmpty;

        /// <summary>
        /// 取出全部待输出行，追加到 <paramref name="currentText"/> 并按
        /// <paramref name="maxLines"/> 截断（保留最新的那些行）。
        /// 无新内容时原样返回入参，调用方可据此跳过属性通知。必须在 UI 线程调用。
        /// </summary>
        public string Flush(string currentText, int maxLines)
        {
            var appended = new StringBuilder();
            while (_pending.TryDequeue(out var line)) appended.AppendLine(line);

            if (appended.Length == 0) return currentText;

            var text = (currentText ?? "") + appended;
            if (maxLines <= 0) return text;

            var lines = text.Split('\n');
            if (lines.Length <= maxLines) return text;

            return string.Join("\n", lines.Skip(lines.Length - maxLines));
        }
    }
}
