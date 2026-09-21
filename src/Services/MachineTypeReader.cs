using System;
using System.IO;

namespace MotionApiTester.Services
{
    /// <summary>
    /// MachineType.json 读取器
    /// 从 D:\MotionConfig\ConfigHardware\MachineType.json 读取机型字符串
    /// </summary>
    public static class MachineTypeReader
    {
        public const string DefaultPath = @"D:\MotionConfig\ConfigHardware\MachineType.json";

        /// <summary>读取机型字符串，失败返回 null</summary>
        public static string Read(string path = null)
        {
            if (path == null) path = DefaultPath;

            try
            {
                if (!File.Exists(path))
                    return null;

                var json = File.ReadAllText(path).Trim();
                // 去掉可能的引号和空白
                json = json.Trim('"', ' ', '\n', '\r', '\t');
                return string.IsNullOrEmpty(json) ? null : json;
            }
            catch
            {
                return null;
            }
        }
    }
}
