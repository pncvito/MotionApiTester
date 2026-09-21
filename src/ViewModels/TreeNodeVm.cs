using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MotionApiTester.Models;

namespace MotionApiTester.ViewModels
{
    /// <summary>
    /// 树节点视图模型(用于 TreeView 绑定)
    /// 层级:Assembly → Namespace → Type → Group(构造函数/方法/属性/字段) → Member
    /// </summary>
    public class TreeNodeVm : INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public string Label { get; set; } = "";
        public string Icon { get; set; } = "";
        public string IconColor { get; set; } = "#0078D4";
        public string NodeKind { get; set; } = ""; // Assembly/Namespace/Type/Group/Method/Property/Field/Constructor
        public string Badge { get; set; } = "";
        public object Payload { get; set; } // 原始数据(ApiMethod/ApiProperty/ApiField/ApiType/ApiAssembly)

        public ObservableCollection<TreeNodeVm> Children { get; } = new ObservableCollection<TreeNodeVm>();

        /// <summary>
        /// 展开状态。TreeViewItem 的 IsExpanded 绑定到它（见 MainWindow.xaml 的隐式样式），
        /// 所以"展开 / 折叠所有"只需遍历数据源 —— 靠 ItemContainerGenerator 去取容器的话，
        /// 虚拟化 + 折叠子树下的节点根本没实化，取不到容器，实际只能展开一两层。
        /// </summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>对 ApiMethod 的快捷访问</summary>
        public ApiMethod Method => Payload as ApiMethod;

        /// <summary>对 ApiProperty 的快捷访问</summary>
        public ApiProperty Property => Payload as ApiProperty;

        /// <summary>对 ApiField 的快捷访问</summary>
        public ApiField Field => Payload as ApiField;

        public override string ToString() => $"{NodeKind}: {Label}";
    }
}