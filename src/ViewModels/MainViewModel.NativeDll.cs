using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MotionApiTester.Models;

namespace MotionApiTester.ViewModels
{
    /// <summary>原生 DLL 扫描、导出表、P/Invoke 模板生成与复制。</summary>
    public partial class MainViewModel
    {
        // ============== 导出函数（新增） ==============

        private NativeExportInfo _selectedNativeExport;
        private string _nativeExportFilter = "";

        /// <summary>当前选中原生 DLL 的导出函数（已按过滤词裁剪）</summary>
        public ObservableCollection<NativeExportInfo> NativeExports { get; } = new ObservableCollection<NativeExportInfo>();

        /// <summary>选中的导出函数 —— 选中即按它生成 P/Invoke 模板</summary>
        public NativeExportInfo SelectedNativeExport
        {
            get => _selectedNativeExport;
            set { if (SetProperty(ref _selectedNativeExport, value)) UpdatePInvokeTemplate(); }
        }

        /// <summary>导出函数过滤词（按名字或序号）</summary>
        public string NativeExportFilter
        {
            get => _nativeExportFilter;
            set { if (SetProperty(ref _nativeExportFilter, value)) RefreshNativeExports(); }
        }

        /// <summary>是否有可展示的导出函数（控制"没有导出表"空状态）</summary>
        public bool HasNativeExports => NativeExports.Count > 0;

        /// <summary>导出函数概要（共 N 个 / 显示 M 个）</summary>
        public string NativeExportSummary
        {
            get
            {
                var dll = SelectedNativeDll;
                if (dll == null) return "";
                if (dll.Exports.Count == 0) return "（这个 DLL 没有导出表，或解析不出）";

                return string.IsNullOrWhiteSpace(_nativeExportFilter)
                    ? $"共 {dll.Exports.Count} 个"
                    : $"显示 {NativeExports.Count} / {dll.Exports.Count} 个";
            }
        }

        /// <summary>切换选中的原生 DLL：清掉上一个 DLL 的导出选中态并重建列表</summary>
        private void OnSelectedNativeDllChanged()
        {
            _selectedNativeExport = null;
            OnPropertyChanged(nameof(SelectedNativeExport));

            RefreshNativeExports();
            UpdatePInvokeTemplate();
        }

        /// <summary>按过滤词重建导出函数列表（纯内存遍历，不碰反射）</summary>
        private void RefreshNativeExports()
        {
            NativeExports.Clear();

            var dll = SelectedNativeDll;
            if (dll != null)
            {
                var keyword = (_nativeExportFilter ?? "").Trim().ToLowerInvariant();
                foreach (var export in dll.Exports)
                {
                    if (keyword.Length == 0 || export.SearchKey.Contains(keyword))
                        NativeExports.Add(export);
                }
            }

            OnPropertyChanged(nameof(HasNativeExports));
            OnPropertyChanged(nameof(NativeExportSummary));
        }

        // ============== 扫描 ==============

        /// <summary>扫描原生 DLL（跳过可反射的 .NET 程序集，只留原生 PE），并解析各自的导出表</summary>
        private void ScanNativeDlls(string directory)
        {
            NativeDlls.Clear();
            SelectedNativeDll = null;

            try
            {
                if (!Directory.Exists(directory)) return;

                // 角色识别只需扫一次目录，不要放进循环里重复扫描
                var roles = _scanner.Scan(directory).DllRoles;

                foreach (var dll in Directory.GetFiles(directory, "*.dll"))
                {
                    var name = Path.GetFileName(dll);
                    if (IsDotNetAssembly(dll)) continue;            // .NET 程序集可反射，不列入原生
                    if (!_nativeInspector.IsValidPeFile(dll)) continue;

                    var role = roles.TryGetValue(name, out var r) ? r : DllRole.ThirdParty;

                    var info = new NativeDllInfo
                    {
                        Name = name,
                        Path = dll,
                        Architecture = _nativeInspector.GetArchitecture(dll),
                        Role = role.ToString()
                    };

                    // 导出表：设备侧的原生 SDK（电机 / 相机）只有靠这个才知道有哪些函数可调
                    info.Exports = _nativeInspector.ReadExports(dll);
                    NativeDlls.Add(info);

                    AppendLog(info.Exports.Count > 0
                        ? $"✓ {name}: 解析到 {info.Exports.Count} 个导出函数"
                        : $"· {name}: 没有可解析的导出表");
                }

                OnPropertyChanged(nameof(HasNativeDlls));

                if (NativeDlls.Count > 0)
                {
                    StatusText += $" | ⚙ {NativeDlls.Count} 原生 DLL";
                    SelectedNativeDll = NativeDlls[0];   // 自动选中首个，直接出导出表与模板
                }
            }
            catch (Exception ex)
            {
                StatusText += $" | ⚠️ 原生 DLL 扫描失败: {ex.Message}";
            }
        }

        /// <summary>重新扫描原生 DLL（工具栏/右栏按钮）</summary>
        private void RefreshNativeDlls()
        {
            if (string.IsNullOrEmpty(CurrentLoadedPath) || !Directory.Exists(CurrentLoadedPath))
            {
                StatusText = "⚠️ 尚未加载设备目录，无法扫描原生 DLL";
                return;
            }

            ScanNativeDlls(CurrentLoadedPath);
            StatusText = NativeDlls.Count > 0
                ? $"✅ 已刷新原生 DLL：{NativeDlls.Count} 个"
                : "当前目录下未发现原生 DLL";
        }

        /// <summary>切换到右栏"原生 DLL"标签页（工具栏按钮命令）</summary>
        private void ShowNativeDlls()
        {
            RightTabIndex = 1;
            if (NativeDlls.Count == 0)
                StatusText = "当前设备目录下未发现原生 DLL（LTSMC.dll 等）";
        }

        /// <summary>根据当前选中的原生 DLL / 导出函数更新 P/Invoke 模板</summary>
        private void UpdatePInvokeTemplate()
        {
            if (_selectedNativeDll == null)
            {
                PInvokeTemplate = "// ← 选中左侧 DLL 查看 P/Invoke 模板";
                return;
            }

            try
            {
                PInvokeTemplate = _nativeInspector.GeneratePInvokeTemplate(
                    _selectedNativeDll.Path, _selectedNativeExport, _selectedNativeDll.Exports.Count);
            }
            catch (Exception ex)
            {
                PInvokeTemplate = $"// ❌ 生成失败: {ex.Message}";
            }
        }

        /// <summary>复制 P/Invoke 模板到剪贴板</summary>
        private void CopyPInvoke()
        {
            if (string.IsNullOrEmpty(PInvokeTemplate)) return;

            try
            {
                Clipboard.SetText(PInvokeTemplate);
                StatusText = "✅ P/Invoke 模板已复制";
            }
            catch (Exception ex)
            {
                StatusText = $"❌ 复制失败: {ex.Message}";
            }
        }
    }
}
