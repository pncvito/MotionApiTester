using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionApiTester.Models;
using MotionApiTester.Services;

namespace MotionApiTester.Views
{
    public partial class DeviceWizardWindow : Window
    {
        public DeviceProfile ResultDevice { get; private set; }

        private DeviceDirectoryScanner _scanner = new DeviceDirectoryScanner();
        private DeviceScanResult _lastScan;

        public DeviceWizardWindow()
        {
            InitializeComponent();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "选择 OptoFidelity 设备 DLL 所在目录";
                fbd.ShowNewFolderButton = false;
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    TbDirectory.Text = fbd.SelectedPath;
                }
            }
        }

        private void TbDirectory_TextChanged(object sender, TextChangedEventArgs e)
        {
            var dir = TbDirectory.Text?.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                BorderScanResult.Visibility = Visibility.Collapsed;
                CbModelDll.Items.Clear();
                BtnAdd.IsEnabled = false;
                return;
            }

            try
            {
                _lastScan = _scanner.Scan(dir);

                int dllCount = _lastScan.AllFiles.Count(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("📂 已扫描: " + dir);
                sb.AppendLine("  DLL 总数: " + dllCount);
                sb.AppendLine("  候选机型: " + _lastScan.CandidateModelDlls.Count + " 个");
                sb.AppendLine("  ✅ BaseTester: " + (_lastScan.HasBaseTester ? "是" : "否"));
                sb.AppendLine("  ⚙ LTSMC: " + (_lastScan.HasLtSmc ? "是" : "⚠️ 缺失（设备可能不动）"));
                sb.AppendLine("  📋 log4net: " + (_lastScan.HasLog4net ? "是" : "⚠️ 缺失"));
                TbScanResult.Text = sb.ToString();
                BorderScanResult.Visibility = Visibility.Visible;

                CbModelDll.Items.Clear();
                foreach (var m in _lastScan.CandidateModelDlls)
                    CbModelDll.Items.Add(m);

                if (_lastScan.CandidateModelDlls.Count >= 1)
                {
                    CbModelDll.SelectedIndex = 0;
                    BtnAdd.IsEnabled = _lastScan.HasBaseTester;
                }
                else
                {
                    BtnAdd.IsEnabled = false;
                }

                // 自动读取 MachineType.json
                if (string.IsNullOrEmpty(TbMachineType.Text))
                {
                    var mt = MachineTypeReader.Read();
                    if (!string.IsNullOrEmpty(mt))
                        TbMachineType.Text = mt;
                }

                // 自动填设备名
                if (string.IsNullOrEmpty(TbName.Text))
                {
                    TbName.Text = new DirectoryInfo(dir.TrimEnd('\\', '/')).Name;
                }
            }
            catch (Exception ex)
            {
                BorderScanResult.Visibility = Visibility.Visible;
                TbScanResult.Text = "❌ 扫描失败: " + ex.Message;
                BtnAdd.IsEnabled = false;
            }
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            var dir = TbDirectory.Text?.Trim();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show("请选择有效的 DLL 目录。", "验证失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (CbModelDll.SelectedItem == null)
            {
                MessageBox.Show("请选择机型 DLL。", "验证失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(TbName.Text))
            {
                MessageBox.Show("请输入设备名称。", "验证失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultDevice = new DeviceProfile
            {
                Name = TbName.Text.Trim(),
                Directory = dir,
                ModelDllName = CbModelDll.SelectedItem.ToString(),
                MachineType = TbMachineType.Text?.Trim() ?? ""
            };

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