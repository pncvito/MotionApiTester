namespace MotionApiTester.Models
{
    /// <summary>原生 DLL 信息（仅枚举，不调用）</summary>
    public class NativeDllInfo
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Role { get; set; } = "";

        public string DisplayText => $"{Name} ({Architecture})";
    }
}