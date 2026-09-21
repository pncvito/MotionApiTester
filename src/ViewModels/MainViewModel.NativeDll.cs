using System;
using System.IO;
using System.Windows;
using MotionApiTester.Models;

namespace MotionApiTester.ViewModels
{
    /// <summary>原生 DLL 扫描、P/Invoke 模板生成与复制。</summary>
    public partial class MainViewModel
    {
        /// <summary>扫描原生 DLL（跳过可反射的 .NET 程序集，只留原生 PE）</summary>
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

                    NativeDlls.Add(new NativeDllInfo
                    {
                        Name = name,
                        Path = dll,
                        Architecture = _nativeInspector.GetArchitecture(dll),
                        Role = role.ToString()
                    });
                }

                OnPropertyChanged(nameof(HasNativeDlls));

                if (NativeDlls.Count > 0)
                {
                    StatusText += $" | ⚙ {NativeDlls.Count} 原生 DLL";
                    SelectedNativeDll = NativeDlls[0];   // 自动选中首个，直接出 P/Invoke 模板
                }
            }
            catch (Exception ex)
            {
                StatusText += $" | ⚠️ 原生 DLL 扫描失败: {ex.Message}";
            }
        }

        /// <summary>重新扫描原生 DLL</summary>
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

        /// <summary>根据当前选中的原生 DLL 更新 P/Invoke 模板</summary>
        private void UpdatePInvokeTemplate()
        {
            if (_selectedNativeDll == null)
            {
                PInvokeTemplate = "// ← 选中左侧 DLL 查看 P/Invoke 模板";
                return;
            }

            try
            {
                PInvokeTemplate = _nativeInspector.GeneratePInvokeTemplate(_selectedNativeDll.Path);
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
