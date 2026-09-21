using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 原生 DLL 检查器：PE 头解析（架构）+ 导出表解析（函数名/序号/转发）+ P/Invoke 模板生成。
    ///
    /// <para><b>只读文件，绝不 LoadLibrary</b>：加载原生 DLL 会执行它的 DllMain / 静态初始化，
    /// 等于拿本进程冒险（尤其是电机、相机这类带驱动的 DLL）。所以全部按 PE 规范手工解析字节。</para>
    /// </summary>
    public class NativeDllInspector
    {
        // ============== PE 头 ==============

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

        // ============== 导出表 ==============

        /// <summary>
        /// 解析 PE 导出目录，列出该 DLL 导出的函数。
        ///
        /// <para>能做到这件事的意义：设备侧的电机 / 相机 SDK 全是原生 DLL，
        /// 厂商文档往往只有 C 头文件没有 C# 示例 —— 有了导出表就能知道"到底有哪些函数可调"，
        /// 而不是只看到一个 DLL 名字。</para>
        ///
        /// <para>任何越界 / 畸形结构一律当作"读不出来"（返回已解析到的部分或空表），
        /// 绝不让解析异常影响设备加载。</para>
        /// </summary>
        public List<NativeExportInfo> ReadExports(string filePath)
        {
            var exports = new List<NativeExportInfo>();

            byte[] data;
            try { data = File.ReadAllBytes(filePath); }
            catch { return exports; }

            try
            {
                int pe = ReadInt32(data, 0x3C);
                if (pe <= 0 || ReadUInt32(data, pe) != 0x00004550) return exports;   // "PE\0\0"

                int coff = pe + 4;
                int sectionCount = ReadUInt16(data, coff + 2);
                int optSize = ReadUInt16(data, coff + 16);
                int opt = coff + 20;

                // PE32 / PE32+ 的数据目录起始位置不同（可选头里字段宽度不一样）
                bool is64 = ReadUInt16(data, opt) == 0x20B;
                int dataDir = opt + (is64 ? 112 : 96);

                int exportRva = ReadInt32(data, dataDir);
                int exportSize = ReadInt32(data, dataDir + 4);
                if (exportRva <= 0 || exportSize <= 0) return exports;

                var sections = ReadSections(data, opt + optSize, sectionCount);
                int exportOffset = RvaToOffset(exportRva, sections);
                if (exportOffset <= 0) return exports;

                int baseOrdinal = ReadInt32(data, exportOffset + 16);
                int funcCount = ReadInt32(data, exportOffset + 20);
                int nameCount = ReadInt32(data, exportOffset + 24);
                int funcsOffset = RvaToOffset(ReadInt32(data, exportOffset + 28), sections);
                int namesOffset = RvaToOffset(ReadInt32(data, exportOffset + 32), sections);
                int ordinalsOffset = RvaToOffset(ReadInt32(data, exportOffset + 36), sections);

                if (nameCount <= 0 || namesOffset <= 0 || ordinalsOffset <= 0) return exports;

                for (int i = 0; i < nameCount; i++)
                {
                    int nameOffset = RvaToOffset(ReadInt32(data, namesOffset + i * 4), sections);
                    if (nameOffset <= 0) continue;

                    var raw = ReadAscii(data, nameOffset, 512);
                    if (raw.Length == 0) continue;

                    int ordinalIndex = ReadUInt16(data, ordinalsOffset + i * 2);
                    var export = new NativeExportInfo
                    {
                        RawName = raw,
                        Name = Undecorate(raw),
                        Ordinal = baseOrdinal + ordinalIndex,

                        // x86 stdcall 导出名会被修饰成 _name@12；cdecl 只有 _name。
                        // x64 没有这个区分（统一调用约定），名字也不带修饰。
                        CallingConvention = raw.StartsWith("_", StringComparison.Ordinal) && HasByteSuffix(raw)
                            ? "StdCall"
                            : "Cdecl",
                    };

                    // 转发判定：函数地址落在导出目录自身区间内时，那个 DWORD 其实是字符串 RVA
                    if (funcsOffset > 0 && ordinalIndex >= 0 && ordinalIndex < funcCount)
                    {
                        int funcRva = ReadInt32(data, funcsOffset + ordinalIndex * 4);
                        if (funcRva >= exportRva && funcRva < exportRva + exportSize)
                        {
                            export.IsForwarder = true;
                            int forwardOffset = RvaToOffset(funcRva, sections);
                            export.ForwardTarget = forwardOffset > 0 ? ReadAscii(data, forwardOffset, 512) : "(未知)";
                        }
                    }

                    exports.Add(export);
                }
            }
            catch
            {
                // 畸形 PE：返回已经解析到的部分
            }

            return exports.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ============== P/Invoke 模板 ==============

        /// <summary>
        /// 生成 P/Invoke 代码片段。
        /// <paramref name="export"/> 为 null 时给通用占位模板（还没选函数）。
        /// </summary>
        /// <param name="exportCount">该 DLL 的导出函数总数，仅用于占位模板里的提示</param>
        public string GeneratePInvokeTemplate(string dllPath, NativeExportInfo export = null, int exportCount = 0)
        {
            var fileName = Path.GetFileName(dllPath);
            var arch = GetArchitecture(dllPath);

            if (export == null)
            {
                var hint = exportCount > 0
                    ? $"// 下面「导出函数」列表里有 {exportCount} 个函数，选中一个即可生成对应模板。\n"
                    : "// 本 DLL 没有导出表（或解析不出），只能照厂商头文件手写声明。\n";

                return $"// 由 MotionApiTester 生成 — {fileName} ({arch})\n"
                     + hint
                     + $"[DllImport(\"{fileName}\", CallingConvention = CallingConvention.Cdecl)]\n"
                     + "public static extern int FunctionName(int param1, double param2);\n\n"
                     + "// TODO: 参数类型与返回值必须照厂商 .h 核对 —— PE 导出表里没有签名信息。";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"// 由 MotionApiTester 生成 — {fileName} ({arch})，导出序号 #{export.Ordinal}");

            if (export.IsForwarder)
                sb.AppendLine($"// ⚠️ 这是转发导出（→ {export.ForwardTarget}），本 DLL 里没有实现，直接调会失败。");
            if (export.IsCppMangled)
                sb.AppendLine("// ⚠️ 这是 C++ 修饰名（? 开头），C# 不能直接 DllImport，需要 C++/CLI 包装层。");

            // EntryPoint 用**原始名字**：x86 stdcall 的 _name@12 若省略会导致找不到入口点
            sb.AppendLine($"[DllImport(\"{fileName}\", EntryPoint = \"{export.RawName}\", "
                        + $"CallingConvention = CallingConvention.{export.CallingConvention})]");
            sb.AppendLine($"public static extern int {ToIdentifier(export.Name)}(int param1, double param2);");
            sb.AppendLine();
            sb.AppendLine("// TODO: 参数类型 / 返回值 / CallingConvention 都要照厂商 .h 核对 ——");
            sb.AppendLine("//       导出表只有名字与序号，没有签名。");
            return sb.ToString().TrimEnd();
        }

        /// <summary>把导出名转成合法的 C# 标识符（名字里可能出现 ? @ 等字符）</summary>
        private static string ToIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return "ExportedFunction";

            var sb = new StringBuilder(name.Length + 1);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            var id = sb.ToString();
            return char.IsDigit(id[0]) ? "_" + id : id;
        }

        // ============== 字节读取（全部带边界检查，畸形文件只返回默认值） ==============

        private static int ReadInt32(byte[] d, int offset) =>
            offset >= 0 && offset + 4 <= d.Length ? BitConverter.ToInt32(d, offset) : 0;

        private static uint ReadUInt32(byte[] d, int offset) =>
            offset >= 0 && offset + 4 <= d.Length ? BitConverter.ToUInt32(d, offset) : 0u;

        private static ushort ReadUInt16(byte[] d, int offset) =>
            offset >= 0 && offset + 2 <= d.Length ? BitConverter.ToUInt16(d, offset) : (ushort)0;

        private static string ReadAscii(byte[] d, int offset, int max)
        {
            if (offset < 0 || offset >= d.Length) return "";

            int end = offset;
            while (end < d.Length && end - offset < max && d[end] != 0) end++;
            return Encoding.ASCII.GetString(d, offset, end - offset);
        }

        private struct PeSection
        {
            public int VirtualAddress;
            public int VirtualSize;
            public int RawPointer;
            public int RawSize;
        }

        private static List<PeSection> ReadSections(byte[] data, int offset, int count)
        {
            var sections = new List<PeSection>();
            for (int i = 0; i < count; i++)
            {
                int p = offset + i * 40;                      // IMAGE_SECTION_HEADER 固定 40 字节
                if (p + 40 > data.Length) break;

                sections.Add(new PeSection
                {
                    VirtualSize = ReadInt32(data, p + 8),
                    VirtualAddress = ReadInt32(data, p + 12),
                    RawSize = ReadInt32(data, p + 16),
                    RawPointer = ReadInt32(data, p + 20),
                });
            }
            return sections;
        }

        /// <summary>RVA → 文件偏移；不在任何节里返回 -1</summary>
        private static int RvaToOffset(int rva, List<PeSection> sections)
        {
            if (rva <= 0) return -1;

            foreach (var s in sections)
            {
                int size = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + size)
                    return s.RawPointer + (rva - s.VirtualAddress);
            }
            return -1;
        }

        /// <summary>名字末尾是否是 stdcall 的 @字节数 修饰</summary>
        private static bool HasByteSuffix(string raw)
        {
            int at = raw.LastIndexOf('@');
            if (at <= 0 || at == raw.Length - 1) return false;

            for (int i = at + 1; i < raw.Length; i++)
                if (!char.IsDigit(raw[i])) return false;
            return true;
        }

        /// <summary>去掉 x86 修饰：<c>_smc_board_close@4</c> → <c>smc_board_close</c></summary>
        private static string Undecorate(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw[0] == '?') return raw;   // C++ 修饰名保持原样

            var name = raw[0] == '_' ? raw.Substring(1) : raw;
            if (HasByteSuffix(name))
            {
                int at = name.LastIndexOf('@');
                if (at > 0) name = name.Substring(0, at);
            }
            return name.Length == 0 ? raw : name;
        }
    }
}
