using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using MotionApiTester.Models;
using MotionApiTester.ViewModels;

namespace MotionApiTester
{
    public partial class MainWindow : Window
    {
        /// <summary>无系统标题栏时，提供边缘缩放能力的边框宽度(DIP，与 XAML 中 WindowChrome 一致)</summary>
        private const double ResizeBorderSize = 6;

        public MainWindow()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => HookViewModel();
            StateChanged += MainWindow_StateChanged;
        }

        private MainViewModel Vm => DataContext as MainViewModel;

        // ============== 窗口外壳(无系统标题栏的补偿处理) ==============
        //
        // WindowStyle="None" 之后，拖动区/边缘缩放/系统菜单由 WindowChrome 提供。
        // 但 WindowChrome 在最大化时会把窗口尺寸算成「工作区 + 边框补偿」，
        // 由于客户区等于整个窗口(GlassFrameThickness=0)，内容会溢出屏幕，
        // 右侧与底部各被裁掉一个边框宽度 —— 这就是「最大化后界面显示不全」。
        // 这里在消息层直接接管 WM_GETMINMAXINFO，把最大化尺寸钉死在工作区。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                ApplyMaximizedBounds(hwnd, lParam);
                // 完全接管：阻止 WindowChrome 再往上叠加边框补偿
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref info))
                {
                    // 用「工作区」而非「显示器区域」：最大化后不盖住任务栏
                    mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
                    mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
                    mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                    mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
                }
            }

            // 接管后需自行承担 WindowChrome 原本负责的最小尺寸限制
            double scaleX = 1.0, scaleY = 1.0;
            var target = PresentationSource.FromVisual(this)?.CompositionTarget;
            if (target != null)
            {
                scaleX = target.TransformToDevice.M11;
                scaleY = target.TransformToDevice.M22;
            }
            if (MinWidth > 0) mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth * scaleX);
            if (MinHeight > 0) mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * scaleY);

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        /// <summary>双保险：最大化时去掉 resize 边框(此时用不到边缘缩放)。
        /// ResizeBorderThickness 是 WindowChrome 的实例属性(非附加属性，无静态 setter)，
        /// 用 GetWindowChrome 取回 XAML 上配置的实例再改。</summary>
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome == null) return;
            chrome.ResizeBorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(ResizeBorderSize);
        }

        // ============== Win32 互操作 ==============
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

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

        // ============== 快捷键 ==============
        //
        // 原先走 XAML 的 <Window.InputBindings><KeyBinding Command="{Binding ...}"/>。
        // 用 .verify/wpfprobe 做的最小复现证明这条路不可靠：
        // KeyBinding 是 Freezable、不在可视树上，当它声明在 <Window.DataContext> **之前**时
        // （MainWindow.xaml 原来就是这个顺序），绑定要等窗口首次 Show 之后才解析出来，
        // 而按键又只在 CanExecute 为 true 时才执行 —— 一旦不满足就是"按下去毫无反应"，
        // 既不在输出里报绑定错误，也没有任何反馈，极难定位。
        // 改在窗口层统一拦截 PreviewKeyDown：Vm 一定拿得到，Preview 先于任何子控件
        //（不受焦点位置影响），并且可以对"没选中方法"这类情况给出显式提示。
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Handled) return;

            var vm = Vm;
            if (vm == null) return;

            // 带 Alt 的组合留给系统(Alt+F4 关窗 / Alt+Space 系统菜单)
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            ICommand command = null;
            if (key == Key.F5)
            {
                // F5 的 CanExecute 取决于"是否选中了可调用成员"。
                // 不满足时若无任何反馈，就会被当成"快捷键坏了"，所以这里显式写一条状态栏提示。
                if (!vm.InvokeCommand.CanExecute(null))
                {
                    vm.StatusText = vm.IsInvoking
                        ? "⏳ 正在调用中，请等本次调用结束"
                        : "⚠️ 未选中可调用成员：请先在左侧 API 树里选一个方法或构造函数，再按 F5";
                    e.Handled = true;
                    return;
                }
                command = vm.InvokeCommand;
            }
            else if (ctrl)
            {
                if (key == Key.O) command = vm.LoadDeviceCommand;
                else if (key == Key.L) command = vm.ClearLogCommand;
                else if (key == Key.H) command = vm.ClearHistoryCommand;
                else if (key == Key.F) command = vm.SearchFocusCommand;
            }
            // Esc 只在搜索框有内容时拦截，否则会把 ComboBox 收下拉之类的默认行为吃掉
            else if (key == Key.Escape && !string.IsNullOrEmpty(vm.SearchText))
            {
                command = vm.SearchClearCommand;
            }

            // CanExecute 为 false（例如没选中方法时的 F5）就不标记 Handled，
            // 让按键继续下传，避免"没执行又吃掉了键"
            if (TryRunCommand(command)) e.Handled = true;
        }

        /// <summary>命令可执行则执行并返回 true；command 为 null 或不可执行返回 false</summary>
        private static bool TryRunCommand(ICommand command)
        {
            if (command == null || !command.CanExecute(null)) return false;
            command.Execute(null);
            return true;
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

        /// <summary>
        /// 展开 / 折叠所有节点。
        ///
        /// <para>改走<b>数据</b>：<c>TreeNodeVm.IsExpanded</c> 已绑定到 <c>TreeViewItem.IsExpanded</c>
        /// （见 MainWindow.xaml 的隐式样式）。旧写法用 ItemContainerGenerator 从容器上递归 ——
        /// 树开了虚拟化，折叠子树下的节点根本没实化，取不到容器，实际只能展开一两层，
        /// 表现就是"点了展开所有却几乎没反应"。</para>
        /// </summary>
        private void SetTreeExpansion(bool expand)
        {
            var vm = Vm;
            if (vm == null) return;

            foreach (var root in vm.FilteredTreeRoots) SetExpansion(root, expand);
        }

        private static void SetExpansion(TreeNodeVm node, bool expand)
        {
            node.IsExpanded = expand;
            foreach (var child in node.Children) SetExpansion(child, expand);
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

        /// <summary>
        /// 实时日志：新行追加在末尾，TextBox 默认不跟随滚动 —— 表现为"日志显示不全"。
        /// 仅当视口原本就贴底(或内容还没占满一屏)时自动滚到末尾，
        /// 这样用户手动向上翻阅历史时不会被强行拉回。
        /// </summary>
        private void TbLog_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = sender as TextBox;
            if (tb == null) return;

            bool atBottom = tb.ExtentHeight <= tb.ViewportHeight + 1
                            || tb.VerticalOffset >= tb.ExtentHeight - tb.ViewportHeight - 2;
            if (atBottom) tb.ScrollToEnd();
        }

        /// <summary>由 ViewModel.SearchFocusCommand 调用(Ctrl+F)</summary>
        public void FocusSearchBox()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }
}
