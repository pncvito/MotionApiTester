using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 调用历史管理服务（**按设备隔离**）。
    ///
    /// <para>以前所有设备都写同一个 <c>default/history.json</c>，切换设备后历史混在一起：
    /// 上一台机器的调用记录会出现在当前设备的历史面板里，很容易误判"这个方法是能用的"。</para>
    /// </summary>
    public class HistoryService
    {
        private const string DefaultDeviceId = "default";

        private readonly string _appDataDir;
        private readonly int _maxItems;

        public ObservableCollection<CallHistoryItem> Items { get; } = new ObservableCollection<CallHistoryItem>();

        /// <summary>当前设备的历史标识（机型 DLL 名，已做文件名安全处理）</summary>
        public string CurrentDeviceId { get; private set; } = DefaultDeviceId;

        public HistoryService(int maxItems = 1000)
        {
            _maxItems = maxItems;
            _appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MotionApiTester");

            Directory.CreateDirectory(_appDataDir);
            Load();
        }

        /// <summary>
        /// 切换到指定设备的历史。
        ///
        /// <para>首次遇到某设备、而它自己的历史文件还不存在时，会把老版本的 <c>default</c> 历史
        /// <b>迁移</b>过来一次 —— 不然升级后用户打开工具会以为历史全丢了。</para>
        /// </summary>
        public void SwitchDevice(string deviceId)
        {
            var id = Sanitize(deviceId);
            if (string.IsNullOrEmpty(id)) id = DefaultDeviceId;
            if (string.Equals(id, CurrentDeviceId, StringComparison.OrdinalIgnoreCase)) return;

            Save();                 // 先把上一台设备的历史落盘
            Items.Clear();
            CurrentDeviceId = id;
            Load();
        }

        /// <summary>从磁盘加载当前设备的历史（文件本身即"最新在前"，顺序追加即可）</summary>
        private void Load()
        {
            try
            {
                var path = GetHistoryPath(CurrentDeviceId);

                if (!File.Exists(path) && !IsDefaultDevice)
                {
                    var legacy = GetHistoryPath(DefaultDeviceId);
                    if (File.Exists(legacy))
                    {
                        foreach (var item in Deserialize(legacy)) Items.Add(item);
                        Save();     // 迁到新位置，之后就走新文件了
                        return;
                    }
                }

                foreach (var item in Deserialize(path)) Items.Add(item);
            }
            catch { /* 损坏则忽略 */ }
        }

        private static List<CallHistoryItem> Deserialize(string path)
        {
            if (!File.Exists(path)) return new List<CallHistoryItem>();

            var items = JsonSerializer.Deserialize<List<CallHistoryItem>>(File.ReadAllText(path));
            return items ?? new List<CallHistoryItem>();
        }

        private bool IsDefaultDevice =>
            string.Equals(CurrentDeviceId, DefaultDeviceId, StringComparison.OrdinalIgnoreCase);

        /// <summary>保存当前设备的历史到磁盘</summary>
        public void Save()
        {
            try
            {
                var path = GetHistoryPath(CurrentDeviceId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path,
                    JsonSerializer.Serialize(Items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
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

        /// <summary>清空当前设备的历史（集合清了，落盘由调用方 Save）</summary>
        public void Clear()
        {
            Items.Clear();
        }

        /// <summary>获取历史文件路径（按设备 ID）</summary>
        public string GetHistoryPath(string deviceId)
        {
            var id = Sanitize(deviceId);
            if (string.IsNullOrEmpty(id)) id = DefaultDeviceId;
            return Path.Combine(_appDataDir, "devices", id, "history.json");
        }

        /// <summary>把设备标识清理成安全的目录名（机型 DLL 名一般没问题，但不能赌）</summary>
        private static string Sanitize(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return "";

            var invalid = Path.GetInvalidFileNameChars();
            var chars = deviceId.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }
    }
}
