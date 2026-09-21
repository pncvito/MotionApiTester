using System.Collections.Generic;
using System.Reflection;

namespace MotionApiTester.Models
{
    /// <summary>API 属性（含 getter/setter）</summary>
    public class ApiProperty
    {
        public PropertyInfo PropertyInfo { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool CanRead { get; set; }
        public bool CanWrite { get; set; }
        public bool IsStatic { get; set; }

        public string Signature => $"{(IsStatic ? "static " : "")}{Type} {Name} {{ {(CanRead ? "get; " : "")}{(CanWrite ? "set; " : "")}}}";
    }

    /// <summary>API 字段</summary>
    public class ApiField
    {
        public FieldInfo FieldInfo { get; set; }
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsStatic { get; set; }
        public bool IsLiteral { get; set; }
        public bool IsInitOnly { get; set; }
        public string RawConstantValue { get; set; }

        public string Signature => $"{(IsStatic ? "static " : "")}{(IsLiteral ? "const " : (IsInitOnly ? "readonly " : ""))}{Type} {Name}";
    }
}