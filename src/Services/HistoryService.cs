using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>调用历史管理服务（按设备隔离）</summary>
    public class HistoryService
    {
        private readonly string _appDataDir;
        private readonly int _maxItems;

        public ObservableCollection<CallHistoryItem> Items { get; } = new ObservableCollection<CallHistoryItem>();

        public HistoryService(int maxItems = 1000)
        {
            _maxItems = maxItems;
            _appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MotionApiTester");

            Directory.CreateDirectory(_appDataDir);
            Load();
        }

        /// <summary>从磁盘加载历史</summary>
        private void Load()
        {
            try
            {
                var path = GetHistoryPath("default");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var items = JsonSerializer.Deserialize<List<CallHistoryItem>>(json);
                    if (items != null)
                    {
                        // 文件本身即为"最新在前"的顺序，直接顺序追加；
                        // 用 Insert(0) 会逐条反转，导致每次重启后顺序颠倒。
                        foreach (var item in items)
                            Items.Add(item);
                    }
                }
            }
            catch { /* 损坏则忽略 */ }
        }

        /// <summary>保存历史到磁盘</summary>
        public void Save()
        {
            try
            {
                var path = GetHistoryPath("default");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var json = JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { /* 忽略写入错误 */ }
        }

        /// <summary>记录一次调用</summary>
        public void Record(CallHistoryItem item)
        {
            Items.Insert(0, item); // 最新在前

            // 超出限制时移除旧的
            while (Items.Count > _maxItems)
                Items.RemoveAt(Items.Count - 1);
        }

        /// <summary>清空历史</summary>
        public void Clear()
        {
            Items.Clear();
        }

        /// <summary>获取历史文件路径（按设备 ID）</summary>
        public string GetHistoryPath(string deviceId)
        {
            return Path.Combine(_appDataDir, "devices", deviceId, "history.json");
        }
    }
}
