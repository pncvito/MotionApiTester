using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MotionApiTester.Services
{
    /// <summary>用户设置管理</summary>
    public class SettingsService
    {
        private readonly string _settingsPath;
        public AppSettings Settings { get; set; } = new AppSettings();

        public SettingsService()
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MotionApiTester");
            Directory.CreateDirectory(appDataDir);
            _settingsPath = Path.Combine(appDataDir, "settings.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) Settings = loaded;
                }
            }
            catch { /* 损坏则使用默认 */ }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch { /* 忽略 */ }
        }
    }

    public class AppSettings
    {
        public string Theme { get; set; } = "Light";
        public int LogRetentionLines { get; set; } = 5000;
        public int InvokeTimeoutSeconds { get; set; } = 30;
        public string ExportFormat { get; set; } = "txt";
        public List<string> RecentDirectories { get; set; } = new List<string>();
        public string DefaultDeviceDirectory { get; set; } = @"D:\MotionApiTester\Bin";
    }
}