using System.Collections.Generic;

namespace MotionApiTester.Models
{
    /// <summary>程序集反射结果</summary>
    public class ApiAssembly
    {
        public string Name { get; set; } = "";
        public string Version { get; set; }
        public string Path { get; set; } = "";
        public bool IsCostura { get; set; }
        public int EmbeddedCount { get; set; }
        public List<ApiType> Types { get; set; } = new List<ApiType>();
        public List<string> LoadErrors { get; set; } = new List<string>();
    }

    public class ApiType
    {
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string Kind { get; set; } = ""; // class / interface / struct / enum
        public List<ApiMethod> Constructors { get; set; } = new List<ApiMethod>();
        public List<ApiMethod> Methods { get; set; } = new List<ApiMethod>();
        public List<ApiProperty> Properties { get; set; } = new List<ApiProperty>();
        public List<ApiField> Fields { get; set; } = new List<ApiField>();
        public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
    }
}
