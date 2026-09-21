using System;
using System.Collections.Generic;
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
            RbExportJson.IsChecked = settings.ExportFormat == "json";
            RbExportTxt.IsChecked = settings.ExportFormat != "json";

            TbDeviceDir.Text = settings.DefaultDeviceDirectory ?? "";
            TbExtraPaths.Text = string.Join(Environment.NewLine,
                settings.ExtraDependencySearchPaths ?? new List<string>());

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
            ResultSettings.ExportFormat = RbExportJson.IsChecked == true ? "json" : "txt";

            // 目录不做"必须存在"校验：设备目录可能插在别的机器上或暂时不可达，
            // 拦下来只会让人没法先配好路径。空值 = 自动探测（见 DeviceDirectoryResolver）。
            ResultSettings.DefaultDeviceDirectory = (TbDeviceDir.Text ?? "").Trim().Trim('"');
            ResultSettings.ExtraDependencySearchPaths = ParseLines(TbExtraPaths.Text);

            DialogResult = true;
            Close();
        }

        /// <summary>多行文本 → 目录列表：去空白行、去引号、按不区分大小写去重</summary>
        private static List<string> ParseLines(string text)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return paths;

            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = raw.Trim().Trim('"');
                if (path.Length == 0) continue;
                if (!seen.Add(path)) continue;      // 去重（不区分大小写）
                paths.Add(path);
            }
            return paths;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}