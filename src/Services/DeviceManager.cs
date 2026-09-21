using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>设备管理器（CRUD + 切换 + 持久化）</summary>
    public class DeviceManager
    {
        private readonly string _devicesFilePath;

        public ObservableCollection<DeviceProfile> Devices { get; } = new ObservableCollection<DeviceProfile>();
        /// <summary>当前激活设备。无激活设备时为 null（项目未启用可空上下文，故不加 ? 注解）</summary>
        public DeviceProfile ActiveDevice { get; private set; }

        public DeviceManager()
        {
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MotionApiTester");
            Directory.CreateDirectory(appDataDir);
            _devicesFilePath = Path.Combine(appDataDir, "devices.json");
            Load();
        }

        /// <summary>从 devices.json 加载</summary>
        private void Load()
        {
            try
            {
                if (File.Exists(_devicesFilePath))
                {
                    var json = File.ReadAllText(_devicesFilePath);
                    var devices = JsonSerializer.Deserialize<List<DeviceProfile>>(json);
                    if (devices != null)
                    {
                        foreach (var d in devices)
                            Devices.Add(d);
                    }
                }
            }
            catch { /* 损坏则忽略 */ }
        }

        /// <summary>保存到 devices.json</summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Devices.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_devicesFilePath, json);
            }
            catch { /* 忽略写入错误 */ }
        }

        /// <summary>添加设备</summary>
        public DeviceProfile AddDevice(string directory, string modelDll, string machineType)
        {
            var profile = new DeviceProfile
            {
                Name = Path.GetFileName(directory.TrimEnd('\\', '/')),
                Directory = directory,
                ModelDllName = modelDll,
                MachineType = machineType,
            };
            Devices.Add(profile);
            Save();
            return profile;
        }

        /// <summary>移除设备</summary>
        public void RemoveDevice(DeviceProfile device)
        {
            Devices.Remove(device);
            if (ActiveDevice == device)
                ActiveDevice = null;
            Save();
        }

        /// <summary>激活设备（切换）</summary>
        public void ActivateDevice(DeviceProfile device)
        {
            ActiveDevice = device;
            device.LastUsedAt = DateTime.Now;
            Save();
        }

        /// <summary>检查设备 DLL 是否就绪</summary>
        public (bool Ok, string Message) CheckDeviceReady(DeviceProfile device)
        {
            if (!File.Exists(device.BaseTesterPath))
                return (false, "缺少 OptoFidelity.BaseTester.dll");

            if (!File.Exists(device.ModelDllPath))
                return (false, $"缺少机型 DLL: {device.ModelDllName}");

            var ltSmc = Path.Combine(device.Directory, "LTSMC.dll");
            if (!File.Exists(ltSmc))
                return (true, "⚠️ 缺少 LTSMC.dll — 设备可能无法动作");

            return (true, "✅ 就绪");
        }
    }
}
