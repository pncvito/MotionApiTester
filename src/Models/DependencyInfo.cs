namespace MotionApiTester.Models
{
    /// <summary>方法依赖项(类型/参数/返回)</summary>
    public class DependencyInfo
    {
        public string Kind { get; set; } = ""; // "返回类型", "参数类型", "throws"
        public string Name { get; set; } = "";
    }
}