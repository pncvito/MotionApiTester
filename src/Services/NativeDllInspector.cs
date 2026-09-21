using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MotionApiTester.Services
{
    /// <summary>原生 DLL 检查器（PE 头解析导出表）
    /// 只枚举导出符号，不调用
    /// </summary>
    public class NativeDllInspector
    {
        /// <summary>检查 DLL 是否为有效的 PE 文件（包括 .NET 和原生 C++）</summary>
        public bool IsValidPeFile(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                using var reader = new BinaryReader(fs);

                // MZ 头
                if (reader.ReadUInt16() != 0x5A4D) return false;

                // PE 偏移
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = reader.ReadInt32();

                // PE 签名
                fs.Seek(peOffset, SeekOrigin.Begin);
                if (reader.ReadUInt32() != 0x00004550) return false;

                // Machine 类型
                ushort machine = reader.ReadUInt16();
                return machine == 0x8664 || machine == 0x014c || machine == 0xAA64;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>获取 DLL 架构</summary>
        public string GetArchitecture(string filePath)
        {
            try
            {
                using var fs = File.OpenRead(filePath);
                using var reader = new BinaryReader(fs);

                if (reader.ReadUInt16() != 0x5A4D) return "Unknown";
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = reader.ReadInt32();
                fs.Seek(peOffset + 4, SeekOrigin.Begin);

                ushort machine = reader.ReadUInt16();
                if (machine == 0x8664) return "x64";
                if (machine == 0x014c) return "x86";
                if (machine == 0xAA64) return "ARM64";
                return "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>生成 P/Invoke 代码片段</summary>
        public string GeneratePInvokeTemplate(string dllPath)
        {
            var fileName = Path.GetFileName(dllPath);
            var arch = GetArchitecture(dllPath);

            return $@"// 由 MotionApiTester 生成 — 适用于 {fileName} ({arch})
[DllImport(""{fileName}"", EntryPoint = ""FunctionName"", CallingConvention = CallingConvention.Cdecl)]
public static extern int FunctionName(int param1, double param2);

// TODO: 请根据实际函数签名调整参数类型和返回值";
        }
    }
}
