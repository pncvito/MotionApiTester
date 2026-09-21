using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace MotionApiTester.Models
{
    /// <summary>API 方法信息（含参数值 + 反射入口）</summary>
    public class ApiMethod
    {
        /// <summary>方法反射入口(transient — 由 ReflectionEnumerator 写入,不序列化;构造函数时为 null)</summary>
        public MethodInfo MethodInfo { get; set; }

        /// <summary>构造函数反射入口(普通方法时为 null)</summary>
        public ConstructorInfo ConstructorInfo { get; set; }

        /// <summary>统一反射入口:方法或构造函数</summary>
        public MethodBase Target => (MethodBase)MethodInfo ?? (MethodBase)ConstructorInfo;

        /// <summary>是否为构造函数</summary>
        public bool IsConstructor => ConstructorInfo != null;

        public string Name { get; set; } = "";
        public string ReturnType { get; set; } = "";
        public string DeclaringType { get; set; } = "";
        public string FullName => IsConstructor ? $"{DeclaringType}(构造函数)" : $"{DeclaringType}.{Name}";
        public bool IsStatic { get; set; }
        public bool IsPublic { get; set; }
        public bool HasLoggerParameter { get; set; }
        public bool IsAsync { get; set; }
        public List<ApiParameter> Parameters { get; set; } = new List<ApiParameter>();
        public string Summary { get; set; }

        /// <summary>用于 UI 显示的签名</summary>
        public string Signature
        {
            get
            {
                var paramStr = string.Join(", ", Parameters.Select(p =>
                    p.IsLogger ? "Action<string>" : $"{p.TypeName} {p.Name}"));
                if (IsConstructor)
                    return $"{(ConstructorInfo.IsStatic ? "static " : "")}{DeclaringType}({paramStr})";
                return $"{ReturnType} {Name}({paramStr})";
            }
        }
    }

    /// <summary>API 参数信息(含 INPC 用户输入)</summary>
    public class ApiParameter : INotifyPropertyChanged
    {
        private string _value = "";
        private bool _passNull;

        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public bool IsOptional { get; set; }
        public string DefaultValue { get; set; }
        public bool IsLogger => TypeName.StartsWith("Action<") || TypeName == "Action`1" || TypeName.Contains("Action<string>");

        /// <summary>用户输入值(双向绑定)</summary>
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value ?? "";
                OnPropertyChanged(nameof(Value));
            }
        }

        /// <summary>是否显式传 null</summary>
        public bool PassNull
        {
            get => _passNull;
            set { if (_passNull != value) { _passNull = value; OnPropertyChanged(nameof(PassNull)); } }
        }

        /// <summary>默认值占位符(UI 提示用)</summary>
        public string ValueHint
        {
            get
            {
                if (IsLogger) return "（自动注入日志回调）";
                if (!string.IsNullOrEmpty(DefaultValue)) return $"默认: {DefaultValue}";
                if (IsOptional) return "（可选）";
                return "";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}