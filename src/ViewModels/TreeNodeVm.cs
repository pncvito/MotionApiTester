using System.Collections.ObjectModel;
using MotionApiTester.Models;

namespace MotionApiTester.ViewModels
{
    /// <summary>
    /// 树节点视图模型(用于 TreeView 绑定)
    /// 层级:Assembly → Namespace → Type → Group(构造函数/方法/属性/字段) → Member
    /// </summary>
    public class TreeNodeVm
    {
        public string Label { get; set; } = "";
        public string Icon { get; set; } = "";
        public string IconColor { get; set; } = "#0078D4";
        public string NodeKind { get; set; } = ""; // Assembly/Namespace/Type/Group/Method/Property/Field/Constructor
        public string Badge { get; set; } = "";
        public object Payload { get; set; } // 原始数据(ApiMethod/ApiProperty/ApiField/ApiType/ApiAssembly)

        public ObservableCollection<TreeNodeVm> Children { get; } = new ObservableCollection<TreeNodeVm>();

        /// <summary>对 ApiMethod 的快捷访问</summary>
        public ApiMethod Method => Payload as ApiMethod;

        /// <summary>对 ApiProperty 的快捷访问</summary>
        public ApiProperty Property => Payload as ApiProperty;

        /// <summary>对 ApiField 的快捷访问</summary>
        public ApiField Field => Payload as ApiField;

        public override string ToString() => $"{NodeKind}: {Label}";
    }
}