using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>设备 DLL 目录扫描器</summary>
    public class DeviceDirectoryScanner
    {
        public DeviceScanResult Scan(string deviceDirectory)
        {
            var result = new DeviceScanResult
            {
                Directory = deviceDirectory,
                AllFiles = Directory.GetFiles(deviceDirectory).Select(Path.GetFileName).ToList()
            };

            var dllFiles = Directory.GetFiles(deviceDirectory, "*.dll")
                .Select(Path.GetFileName).ToList();

            foreach (var dll in dllFiles)
                result.DllRoles[dll] = DetectRole(dll);

            // 候选机型 DLL（OptoFidelity.{Model}.dll，排除 BaseTester）
            result.CandidateModelDlls = dllFiles
                .Where(d => d.StartsWith("OptoFidelity.", StringComparison.OrdinalIgnoreCase)
                         && !d.Equals("OptoFidelity.BaseTester.dll", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 关键 DLL 检查
            result.HasBaseTester = dllFiles.Contains("OptoFidelity.BaseTester.dll", StringComparer.OrdinalIgnoreCase);
            result.HasLtSmc = dllFiles.Any(d => d.Equals("LTSMC.dll", StringComparison.OrdinalIgnoreCase));
            result.HasLog4net = dllFiles.Any(d => d.Equals("log4net.dll", StringComparison.OrdinalIgnoreCase));

            return result;
        }

        private static DllRole DetectRole(string fileName)
        {
            if (fileName.Equals("OptoFidelity.BaseTester.dll", StringComparison.OrdinalIgnoreCase))
                return DllRole.Base;

            if (fileName.StartsWith("OptoFidelity.", StringComparison.OrdinalIgnoreCase))
                return DllRole.Device;

            if (fileName.StartsWith("Plugin.", StringComparison.OrdinalIgnoreCase))
                return DllRole.DeviceEmbedded;

            if (IsHardwareDll(fileName))
                return DllRole.Hardware;

            if (fileName.Equals("log4net.dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("NLog.dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("Serilog.dll", StringComparison.OrdinalIgnoreCase))
                return DllRole.Log;

            if (fileName.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("msvcr", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("mfc90.dll", StringComparison.OrdinalIgnoreCase))
                return DllRole.Runtime;

            return DllRole.ThirdParty;
        }

        private static bool IsHardwareDll(string fileName) =>
            fileName.Equals("LTSMC.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("GxIAP", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("GenApi_", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("CL3_IF.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("DxImageProc.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("RseeController.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("AxNICfg.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("SGIFPJ.dll", StringComparison.OrdinalIgnoreCase);
    }

    public class DeviceScanResult
    {
        public string Directory { get; set; } = "";
        public List<string> AllFiles { get; set; } = new List<string>();
        public Dictionary<string, DllRole> DllRoles { get; set; } = new Dictionary<string, DllRole>();
        public List<string> CandidateModelDlls { get; set; } = new List<string>();
        public bool HasBaseTester { get; set; }
        public bool HasLtSmc { get; set; }
        public bool HasLog4net { get; set; }
    }
}
