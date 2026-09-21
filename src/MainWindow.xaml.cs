using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MotionApiTester.Models;
using MotionApiTester.ViewModels;

namespace MotionApiTester
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => HookViewModel();
        }

        private MainViewModel Vm => DataContext as MainViewModel;

        private void HookViewModel()
        {
            if (Vm == null) return;
            Vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.SelectedTreeNode))
                    UpdateIlSignature();

                if (e.PropertyName == nameof(MainViewModel.SelectedTreeNode) ||
                    e.PropertyName == nameof(MainViewModel.ResultText) ||
                    e.PropertyName == nameof(MainViewModel.ResultSuccessFlag) ||
                    e.PropertyName == nameof(MainViewModel.LastElapsedMs))
                    UpdateResultCardStyle();
            };
        }

        // ============== 树选中 ==============
        private void TvApi_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (Vm != null && e.NewValue is TreeNodeVm node)
            {
                Vm.SelectedTreeNode = node;
            }
        }

        // ============== IL 签名(支持方法与构造函数) ==============
        private void UpdateIlSignature()
        {
            var node = Vm?.SelectedTreeNode;
            var target = node?.Method?.Target;

            if (target is MethodInfo mi)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{mi.ReturnType.FullName} {mi.DeclaringType.FullName}::{mi.Name}(");
                var pars = mi.GetParameters();
                for (int i = 0; i < pars.Length; i++)
                {
                    var p = pars[i];
                    sb.Append($"    [{i}] {p.ParameterType.FullName} {p.Name}");
                    if (p.HasDefaultValue) sb.Append($" = {p.DefaultValue}");
                    sb.AppendLine(i < pars.Length - 1 ? "," : "");
                }
                sb.AppendLine(")");
                sb.AppendLine();
                sb.AppendLine("Attributes:");
                sb.AppendLine($"  IsStatic: {mi.IsStatic}");
                sb.AppendLine($"  IsPublic: {mi.IsPublic}");
                sb.AppendLine($"  IsVirtual: {mi.IsVirtual}");
                sb.AppendLine($"  IsAbstract: {mi.IsAbstract}");
                sb.AppendLine($"  IsFinal: {mi.IsFinal}");
                sb.AppendLine($"  IsGenericMethod: {mi.IsGenericMethod}");
                sb.AppendLine($"  IsGenericMethodDefinition: {mi.IsGenericMethodDefinition}");
                if (mi.IsGenericMethod)
                    sb.AppendLine($"  GenericArguments: {string.Join(", ", mi.GetGenericArguments().Select(t => t.Name))}");
                TbIlSignature.Text = sb.ToString();
            }
            else if (target is ConstructorInfo ci)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{ci.DeclaringType.FullName}::{ci.DeclaringType.Name}(");
                var pars = ci.GetParameters();
                for (int i = 0; i < pars.Length; i++)
                {
                    var p = pars[i];
                    sb.Append($"    [{i}] {p.ParameterType.FullName} {p.Name}");
                    if (p.HasDefaultValue) sb.Append($" = {p.DefaultValue}");
                    sb.AppendLine(i < pars.Length - 1 ? "," : "");
                }
                sb.AppendLine(")");
                sb.AppendLine();
                sb.AppendLine("Attributes:");
                sb.AppendLine("  IsConstructor: True");
                sb.AppendLine($"  IsStatic: {ci.IsStatic}");
                sb.AppendLine($"  IsPublic: {ci.IsPublic}");
                TbIlSignature.Text = sb.ToString();
            }
            else if (node?.Property?.PropertyInfo != null)
            {
                var pi = node.Property.PropertyInfo;
                TbIlSignature.Text = $"Property: {pi.PropertyType.FullName} {pi.DeclaringType.FullName}::{pi.Name}\r\nCanRead: {pi.CanRead}\r\nCanWrite: {pi.CanWrite}\r\nIsStatic: {pi.GetGetMethod()?.IsStatic ?? false}";
            }
            else if (node?.Field?.FieldInfo != null)
            {
                var fi = node.Field.FieldInfo;
                TbIlSignature.Text = $"Field: {fi.FieldType.FullName} {fi.DeclaringType.FullName}::{fi.Name}\r\nIsStatic: {fi.IsStatic}\r\nIsLiteral: {fi.IsLiteral}\r\nIsInitOnly: {fi.IsInitOnly}";
            }
            else
            {
                TbIlSignature.Text = "(未选中)";
            }
        }

        // ============== 结果卡片颜色(走主题令牌,深色主题下同样正确) ==============
        private void UpdateResultCardStyle()
        {
            if (Vm == null) return;

            bool hasResult = !string.IsNullOrEmpty(Vm.ResultText);
            bool ok = hasResult && Vm.ResultSuccessFlag;

            ResultHead.SetResourceReference(Border.BackgroundProperty, ok ? "SuccessBgBrush" : "DangerBgBrush");
            TbResultHead.SetResourceReference(TextBlock.ForegroundProperty, ok ? "SuccessFgBrush" : "DangerFgBrush");

            TbResultHead.Text = !hasResult
                ? "等待调用"
                : (ok ? $"✓ 调用成功 · 耗时 {Vm.LastElapsedMs} ms" : $"✗ 调用失败 · 耗时 {Vm.LastElapsedMs} ms");
        }

        // ============== 按钮事件 ==============
        private void MenuCopySignature_Click(object sender, RoutedEventArgs e)
        {
            var text = GetCopyText();
            if (text != null)
            {
                Clipboard.SetText(text);
                if (Vm != null) Vm.StatusText = $"✅ 已复制: {text}";
            }
        }

        private void MenuCopyFullName_Click(object sender, RoutedEventArgs e)
        {
            var node = Vm?.SelectedTreeNode;
            string text = null;
            if (node?.Method != null) text = node.Method.FullName;
            else if (node?.Payload is ApiType t) text = t.FullName;
            else if (node != null) text = node.Label;

            if (text != null)
            {
                Clipboard.SetText(text);
                if (Vm != null) Vm.StatusText = $"✅ 已复制: {text}";
            }
        }

        private string GetCopyText()
        {
            var node = Vm?.SelectedTreeNode;
            if (node?.Method != null) return node.Method.Signature;
            if (node?.Property != null) return node.Property.Signature;
            if (node?.Field != null) return node.Field.Signature;
            return node?.Label;
        }

        private void MenuExpandAll_Click(object sender, RoutedEventArgs e) => SetTreeExpansion(true);
        private void MenuCollapseAll_Click(object sender, RoutedEventArgs e) => SetTreeExpansion(false);

        private void SetTreeExpansion(bool expand)
        {
            foreach (var item in TvApi.Items)
            {
                if (TvApi.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem tvi)
                    SetExpansion(tvi, expand);
            }
        }

        private static void SetExpansion(TreeViewItem item, bool expand)
        {
            item.IsExpanded = expand;
            foreach (var child in item.Items)
            {
                if (item.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem tvi)
                    SetExpansion(tvi, expand);
            }
        }

        private void BtnCopySignature_Click(object sender, RoutedEventArgs e)
        {
            MenuCopySignature_Click(sender, e);
        }

        private void BtnFillDefaults_Click(object sender, RoutedEventArgs e)
        {
            var node = Vm?.SelectedTreeNode;
            if (node?.Method == null) return;
            foreach (var p in node.Method.Parameters)
            {
                if (!string.IsNullOrEmpty(p.DefaultValue))
                    p.Value = p.DefaultValue;
            }
        }

        private void BtnClearParams_Click(object sender, RoutedEventArgs e)
        {
            var node = Vm?.SelectedTreeNode;
            if (node?.Method == null) return;
            foreach (var p in node.Method.Parameters)
                p.Value = "";

            if (Vm != null) Vm.JsonArgsOverride = "";
        }

        // ============== 窗口控制(无系统标题栏,按钮在自绘标题栏内) ==============
        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>由 ViewModel.SearchFocusCommand 调用(Ctrl+F)</summary>
        public void FocusSearchBox()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }
}
