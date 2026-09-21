using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace MotionApiTester.Services
{
    /// <summary>实时日志服务</summary>
    public class LogService
    {
        private readonly Dispatcher _dispatcher;
        private readonly int _maxLines;

        public ObservableCollection<string> Lines { get; } = new ObservableCollection<string>();
        public bool IsPaused { get; set; }

        public LogService(Dispatcher dispatcher, int maxLines = 5000)
        {
            _dispatcher = dispatcher;
            _maxLines = maxLines;
        }

        /// <summary>追加日志行</summary>
        public void Append(string message)
        {
            if (IsPaused) return;

            _dispatcher.BeginInvoke(new Action(() =>
            {
                Lines.Add(message);

                // 超出最大行数时截断旧的
                while (Lines.Count > _maxLines)
                    Lines.RemoveAt(0);
            }));
        }

        /// <summary>清空日志</summary>
        public void Clear()
        {
            _dispatcher.Invoke(() => Lines.Clear());
        }

        /// <summary>导出日志到文件</summary>
        public void Export(string filePath)
        {
            System.IO.File.WriteAllLines(filePath, Lines);
        }
    }
}
