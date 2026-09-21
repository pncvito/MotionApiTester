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
        /// <summary>设备 DLL 目录。留空表示自动探测（见 DeviceDirectoryResolver）</summary>
        public string DefaultDeviceDirectory { get; set; } = "";

        /// <summary>
        /// 额外的依赖搜索目录（交给 DependencyResolver）。
        /// 用于设备 DLL 依赖散落在其它构建产物的场景，避免把路径写死在代码里。
        /// </summary>
        public List<string> ExtraDependencySearchPaths { get; set; } = new List<string>();
    }
}