using System.Collections.Generic;

namespace MotionApiTester.Models
{
    /// <summary>原生 DLL 信息（仅枚举，不调用）</summary>
    public class NativeDllInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Role { get; set; } = "";

        /// <summary>导出函数表（解析 PE 导出目录得到；解析不出来时是空表，不影响其它功能）</summary>
        public List<NativeExportInfo> Exports { get; set; } = new List<NativeExportInfo>();

        /// <summary>导出函数个数徽章文本</summary>
        public string ExportSummary =>
            Exports.Count > 0 ? $"{Exports.Count} 导出" : "无导出表";

        public string DisplayText => $"{Name} ({Architecture})";

        /// <summary>列表项的自动化名取它（否则屏幕阅读器 / UI 自动化会把类型全名念出来）</summary>
        public override string ToString() => DisplayText;
    }

    /// <summary>
    /// 原生 DLL 的一个导出函数。
    ///
    /// <para>导出表里只有"名字 / 序号 / 地址"，<b>没有签名</b>（参数类型、返回值）——
    /// 所以模板只能生成骨架，参数得照厂商 .h 核对。这一点必须在界面上说清楚，
    /// 否则用户会以为生成出来的东西可以直接用。</para>
    /// </summary>
    public class NativeExportInfo
    {
        /// <summary>去掉 x86 修饰后的名字（给人看、用于生成 C# 方法名）</summary>
        public string Name { get; set; } = "";

        /// <summary>导出表里的原始名字。⚠️ DllImport 的 EntryPoint 必须用它 —— x86 stdcall 的名字带 <c>_</c> 前缀与 <c>@N</c> 后缀</summary>
        public string RawName { get; set; } = "";

        /// <summary>导出序号</summary>
        public int Ordinal { get; set; }

        /// <summary>是否转发到别的 DLL（转发项在本 DLL 里没有实现，调不到）</summary>
        public bool IsForwarder { get; set; }

        /// <summary>转发目标（如 <c>NTDLL.RtlAllocateHeap</c>）</summary>
        public string ForwardTarget { get; set; } = "";

        /// <summary>由名字修饰推断的调用约定（StdCall / Cdecl）</summary>
        public string CallingConvention { get; set; } = "Cdecl";

        /// <summary>是否 C++ 修饰名（<c>?</c> 开头）—— C# 无法直接 DllImport</summary>
        public bool IsCppMangled => RawName.StartsWith("?");

        public string DisplayText
        {
            get
            {
                var text = $"{Name}   #{Ordinal}";
                if (IsForwarder) text += $"   → {ForwardTarget}";
                return text;
            }
        }

        /// <summary>供输入框过滤（小写，名字 + 序号）</summary>
        public string SearchKey => (Name + " " + Ordinal).ToLowerInvariant();

        /// <summary>列表项的自动化名取它（否则屏幕阅读器 / UI 自动化会把类型全名念出来）</summary>
        public override string ToString() => DisplayText;
    }
}
