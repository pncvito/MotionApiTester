using System;

namespace MotionApiTester.Models
{
    /// <summary>调用历史记录项</summary>
    public class CallHistoryItem
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string MethodName { get; set; } = "";
        public string Parameters { get; set; } = "";
        public string Result { get; set; } = "";
        public long ElapsedMs { get; set; }
        public bool Success { get; set; }

        public string DisplayText =>
            $"[{Timestamp:HH:mm:ss}] {MethodName} → {(Success ? "✓" : "❌")} {Result} ({ElapsedMs}ms)";
    }
}
