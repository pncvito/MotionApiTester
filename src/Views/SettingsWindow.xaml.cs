using System.Windows;
using MotionApiTester.Services;
using MotionApiTester.ViewModels;

namespace MotionApiTester.Views
{
    public partial class SettingsWindow : Window
    {
        public AppSettings ResultSettings { get; private set; }

        /// <summary>
        /// <paramref name="vm"/> 用于「设备」一节：直接复用主窗口的
        /// Devices / SelectedDevice / 切换·添加·移除 命令，不另建一套状态。
        /// </summary>
        public SettingsWindow(AppSettings settings, MainViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;

            // 加载当前值
            switch (settings.Theme)
            {
                case "Dark": RbDark.IsChecked = true; break;
                case "System": RbSystem.IsChecked = true; break;
                default: RbLight.IsChecked = true; break;
            }
            TbMaxLines.Text = settings.LogRetentionLines.ToString();
            TbTimeout.Text = settings.InvokeTimeoutSeconds.ToString();
            CbExportFormat.SelectedIndex = settings.ExportFormat == "json" ? 1 : 0;

            ResultSettings = settings;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // 验证 + 保存
            if (!int.TryParse(TbMaxLines.Text, out int maxLines) || maxLines < 100 || maxLines > 100000)
            {
                MessageBox.Show("日志最大行数必须是 100-100000 之间的整数。", "验证失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TbMaxLines.Focus();
                return;
            }

            if (!int.TryParse(TbTimeout.Text, out int timeout) || timeout < 5 || timeout > 600)
            {
                MessageBox.Show("超时必须是 5-600 之间的整数（秒）。", "验证失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TbTimeout.Focus();
                return;
            }

            ResultSettings.Theme = RbDark.IsChecked == true ? "Dark"
                                 : RbSystem.IsChecked == true ? "System"
                                 : "Light";
            ResultSettings.LogRetentionLines = maxLines;
            ResultSettings.InvokeTimeoutSeconds = timeout;
            ResultSettings.ExportFormat = CbExportFormat.SelectedIndex == 1 ? "json" : "txt";

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}