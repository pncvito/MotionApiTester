using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using MotionApiTester.Models;
using MotionApiTester.Services;
using MotionApiTester.Views;

namespace MotionApiTester.ViewModels
{
    /// <summary>设备目录加载 / 卸载、设备增删切换、设置窗口。</summary>
    public partial class MainViewModel
    {
        /// <summary>
        /// 当前设备 DLL 目录。
        /// settings.json 的 DefaultDeviceDirectory 优先；为空或目录失效时由
        /// DeviceDirectoryResolver 按 exe 相对位置探测 —— 代码里不再写死绝对路径。
        /// </summary>
        private string DeviceBinDir =>
            _deviceBinDirCache ??
            (_deviceBinDirCache = DeviceDirectoryResolver.Resolve(_settingsService?.Settings?.DefaultDeviceDirectory));

        /// <summary>启动时自动加载默认设备目录，并把该目录登记为一个"当前设备"</summary>
        private void TryAutoLoadDefaultDevice()
        {
            try
            {
                var deviceDir = DeviceBinDir;
                LoadDeviceFromDirectory(deviceDir);

                if (!IsLoaded || !Directory.Exists(deviceDir)) return;

                var machineType = MachineTypeReader.Read();
                var currentDevice = new DeviceProfile
                {
                    Name = "(当前) " + new DirectoryInfo(deviceDir).Name,
                    Directory = deviceDir,
                    ModelDllName = MachineTypeMatcher.FindBestMatch(
                        machineType ?? "", _scanner.Scan(deviceDir).CandidateModelDlls) ?? "",
                    MachineType = machineType ?? ""
                };

                // 同目录已登记过就复用那一条，并把机型 / 型号 DLL 刷成最新探测结果。
                // 注意必须顺手设为当前选中 —— 否则启动后设备下拉框是空白的，▶/✕ 也是灰的
                // （ComboBox 有项但 SelectedItem 为 null 时就是一片空白，很容易被当成"没加载"）。
                var existing = _devices.FirstOrDefault(d =>
                    string.Equals(d.Directory, currentDevice.Directory, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.MachineType = currentDevice.MachineType;
                    existing.ModelDllName = currentDevice.ModelDllName;
                    SelectedDevice = existing;
                    return;
                }

                _devices.Add(currentDevice);
                SelectedDevice = currentDevice;
            }
            catch (Exception ex)
            {
                LogLoadError("Auto-load", ex);
                StatusText = $"❌ 自动加载失败: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>加载设备按钮命令 —— 从当前设备目录加载</summary>
        private void LoadDevice() => LoadDeviceFromDirectory(DeviceBinDir);

        /// <summary>
        /// 卸载设备。
        /// 说明：程序集一旦载入 AppDomain 就无法真正卸载，这里只清空界面状态与缓存，
        /// 保证重新加载时不会复用旧实例。
        /// </summary>
        private void UnloadDevice()
        {
            // ① 清树：Assemblies 只是数据源之一，TreeView 实际绑的是 FilteredTreeRoots，
            //    必须走 ClearTreeState（内部会重建过滤集合 + 清搜索框 + 清选中节点）
            ClearTreeState();

            // ② 清结果区与参数区
            ResultText = "";
            ResultFullMessage = "—";
            ResultSuccessFlag = false;
            JsonArgsOverride = "";

            // ③ 清机型 / 路径标识，状态栏回到"未加载"
            MachineTypeText = "";
            CurrentLoadedPath = "";

            // ④ 清原生 DLL 区
            NativeDlls.Clear();
            SelectedNativeDll = null;
            PInvokeTemplate = "// ← 选中左侧 DLL 查看 P/Invoke 模板";
            OnPropertyChanged(nameof(HasNativeDlls));

            // ⑤ 释放运行时缓存（程序集本身无法从 AppDomain 卸载，只能保证不复用旧实例）
            IsLoaded = false;
            _loadedAssemblies.Clear();
            _invoker.SetCandidateAssemblies(_loadedAssemblies);
            _dependencyResolver.Clear();
            _invoker.ClearInstances();

            // 还原原生 DLL 搜索路径，避免卸载后仍指向旧设备目录
            NativeSearchPath.SetDeviceDirectory(null, AppendLog);

            // ⑥ Total*Count 是计算属性，需显式通知
            NotifyAssemblyStats();

            StatusText = "已卸载 — 界面已清空(程序集无法从进程卸载，重新加载即可)";
        }

        /// <summary>从指定目录加载设备（扫描 → 匹配机型 → 反射枚举 → 扫描原生 DLL → 应用搜索）</summary>
        public void LoadDeviceFromDirectory(string deviceDir)
        {
            // 清空上一轮状态：树、选中节点、统计数字都要一起归零，
            // 否则中途 return（目录无效 / 机型不匹配）会留下上一次的树和数字。
            Assemblies.Clear();
            NativeDlls.Clear();
            SelectedTreeNode = null;
            CurrentLoadedPath = deviceDir;
            NotifyAssemblyStats();
            StatusText = $"🔍 扫描目录: {deviceDir}";

            // ⚠️ 必须清掉上一轮的字节缓存与**嵌入宿主**登记。
            //    否则上一台设备的 Costura 宿主还挂在体检的"能供出依赖"名单里，
            //    换成一台真正缺依赖的设备时会误报成"全部可解析"。
            _dependencyResolver.Clear();

            // 让依赖解析优先在设备目录内查找
            _dependencyResolver.SetDeviceDirectory(deviceDir);

            // ① 读 MachineType.json
            var machineType = MachineTypeReader.Read();
            MachineTypeText = machineType ?? "(未找到 MachineType.json)";

            // ② 扫描设备目录
            var scanResult = _scanner.Scan(deviceDir);
            StatusText = $"📂 目录: {deviceDir} | DLL: {scanResult.AllFiles.Count(d => d.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))} 个 | 候选机型: {scanResult.CandidateModelDlls.Count} 个";

            if (scanResult.CandidateModelDlls.Count == 0)
            {
                StatusText = "❌ 目录中未找到 OptoFidelity.*.dll（设备机型 DLL）";
                return;
            }

            // ③ 匹配机型 DLL
            string modelDll = null;
            if (machineType != null)
                modelDll = MachineTypeMatcher.FindBestMatch(machineType, scanResult.CandidateModelDlls);
            else if (scanResult.CandidateModelDlls.Count == 1)
                modelDll = scanResult.CandidateModelDlls[0];

            if (modelDll == null)
            {
                StatusText = $"❌ 无法匹配机型 DLL。MachineType={machineType ?? "(无)"}，候选: {string.Join(", ", scanResult.CandidateModelDlls)}";
                return;
            }

            // ④ 校验文件
            var baseDll = Path.Combine(deviceDir, "OptoFidelity.BaseTester.dll");
            var modelPath = Path.Combine(deviceDir, modelDll);

            if (!File.Exists(baseDll))
            {
                StatusText = $"❌ 缺少基类 SDK: {baseDll}";
                return;
            }
            if (!File.Exists(modelPath))
            {
                StatusText = $"❌ 缺少机型 DLL: {modelPath}";
                return;
            }

            // 原生 DLL 无法反射枚举
            if (!IsDotNetAssembly(baseDll))
            {
                StatusText = "⚠️ BaseTester.dll 是原生 C++ DLL，无法反射枚举 API";
                return;
            }
            if (!IsDotNetAssembly(modelPath))
            {
                StatusText = $"⚠️ {modelDll} 是原生 C++ DLL，无法反射枚举 API";
                return;
            }

            StatusText = $"⏳ 加载 {modelDll}...";

            // ⚠️ 先解决「原生」依赖，再碰任何设备侧代码。
            //    设备侧托管 DLL 里的 [DllImport("LTSMC.dll")] 走的是 Windows LoadLibrary
            //    搜索顺序（exe 目录 → System32 → Windows → 当前工作目录 → PATH），
            //    设备目录不在其中任何一项 —— 不设这一步，即便 LTSMC.dll 就躺在设备目录里，
            //    调用时仍会抛 DllNotFoundException(HRESULT 0x8007007E ERROR_MOD_NOT_FOUND)。
            //    ⚠️ 这与托管程序集解析（_dependencyResolver 的 AssemblyResolve）是两套
            //    完全独立的机制，谁都不会替谁兜底。
            NativeSearchPath.SetDeviceDirectory(deviceDir, AppendLog);

            // 先登记两个 DLL 的字节，后续 AssemblyResolve 命中缓存即可直接返回
            var baseBytes = File.ReadAllBytes(baseDll);
            var modelBytes = File.ReadAllBytes(modelPath);
            _dependencyResolver.Register(Assembly.Load(baseBytes).GetName().Name, baseBytes);
            _dependencyResolver.Register(Assembly.Load(modelBytes).GetName().Name, modelBytes);

            // ⑤ 反射枚举（从 byte[] 加载，避免文件被锁定）
            _loadedAssemblies.Clear();
            try
            {
                // ⚠️ 这里的加载顺序是硬约束，不能颠倒：
                //    机型 DLL 是 Costura 嵌入包，BaseTester 的 12 个外部依赖
                //    (CustomCore / Core / CustomLogic / CoreService / HwManager / SystemManager
                //     / Logger / Prism / Prism.Wpf / Prism.DryIoc.Wpf …) 全靠它内嵌的那一份供给。
                //    而 Costura 的解析器挂在宿主程序集的 <Module> 静态构造上 ——
                //    只有宿主 DLL 的代码「第一次被执行」时才注册。
                //    本工具用 Assembly.Load(byte[]) + 纯元数据反射，从不执行设备侧代码，
                //    所以这里必须主动激活一次；且必须在枚举 BaseTester 之前，
                //    否则 CLR 解析 BaseTester 的依赖时会直接抛 FileNotFoundException。
                var modelAsm = Assembly.Load(modelBytes);
                _loadedAssemblies.Add(modelAsm);
                if (CosturaActivator.TryActivate(modelAsm, AppendLog))
                {
                    // ⚠️ 必须登记给体检：这些依赖在磁盘上永远不会出现文件，
                    //    不登记的话体检会把它们全报成"缺失"（见 DependencyResolver.AuditDependencies）。
                    _dependencyResolver.RegisterEmbeddedProvider(modelAsm);
                }

                // 机型 DLL 的解析器已就位，此后 BaseTester 的依赖可从嵌入资源按需解出
                var baseAsm = Assembly.Load(baseBytes);
                _loadedAssemblies.Add(baseAsm);
                Assemblies.Add(_enumerator.Enumerate(baseAsm, baseDll));
                Assemblies.Add(_enumerator.Enumerate(modelAsm, modelPath));
            }
            catch (BadImageFormatException ex)
            {
                LogLoadError("LoadDLL: BadImage", ex);
                StatusText = $"❌ DLL 格式错误（不是有效的 .NET 程序集）: {ex.Message}";
                return;
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 部分类型加载失败 —— Enumerate 内部已从 ex.Types 取到能加载的部分，继续展示
                LogLoadError("LoadDLL: ReflectionType", ex);
                StatusText = $"⚠️ 部分类型加载失败: {ex.LoaderExceptions?.Length ?? 0} 个错误（已尽可能展示）";
            }
            catch (Exception ex)
            {
                LogLoadError("LoadDLL", ex);
                StatusText = $"❌ 加载失败: {ex.GetType().Name}: {ex.Message}";
                return;
            }

            // 把本次加载的程序集交给调用器，用于解析接口 / 抽象类型的具体实现
            _invoker.ClearInstances();
            _invoker.SetCandidateAssemblies(_loadedAssemblies);

            // ⑥ 状态栏统计
            int typeCount = Assemblies.Sum(a => a.Types.Count);
            int methodCount = Assemblies.Sum(a => a.Types.Sum(t => t.Methods.Count));
            int loggerCount = Assemblies.Sum(a => a.Types.Sum(t => t.Methods.Count(m => m.HasLoggerParameter)));
            int embedded = Assemblies.Sum(a => a.EmbeddedCount);
            var errors = Assemblies.SelectMany(a => a.LoadErrors).ToList();

            StatusText = $"✅ {modelDll} | 🔷 {typeCount} 类型 ⚙ {methodCount} 方法 📋 {loggerCount} logger 📦 {embedded} 嵌入";
            if (errors.Any()) StatusText += $" ⚠️ {errors.Count} 个加载错误";
            IsLoaded = true;

            if (!scanResult.HasLtSmc) StatusText += " ⚠️ 缺 LTSMC.dll";

            // ⑦ 依赖体检：把"设备目录缺运行库"一次性说清楚。
            //    少了这一步，缺失的依赖要等用户点到某个成员、由 CLR 抛 FileNotFoundException
            //    才暴露，而且一次只暴露栈顶那一个（例：CustomCore 被 Init 引用）。
            ReportMissingDependencies();

            // ⑧ 扫描原生 DLL ⑨ 应用搜索过滤 ⑩ 刷新统计数字
            ScanNativeDlls(deviceDir);
            RefreshSearch();
            NotifyAssemblyStats();
        }

        /// <summary>
        /// 依赖体检：加载完成后枚举已加载程序集的引用并分类，把「设备目录缺运行库」一次性说清楚。
        /// 返回真正缺失的个数。
        /// </summary>
        private int ReportMissingDependencies()
        {
            var audit = _dependencyResolver.AuditDependencies(_loadedAssemblies);

            if (audit.Missing.Count == 0)
            {
                // 纯 Costura 项目（设备目录只有 4 个文件）走的就是这一支：
                // 依赖文件一个都不在磁盘上，但全部能从宿主内嵌资源解出来。
                AppendLog($"✓ 依赖体检: {audit.Total} 个引用全部可解析（{audit.Summary}）");
                return 0;
            }

            AppendLog($"⚠️ 依赖体检: 当前设备目录缺少 {audit.Missing.Count} 个运行库 —— " +
                      $"{string.Join(", ", audit.Missing)}");
            if (audit.Total > audit.Missing.Count)
                AppendLog($"   （其余 {audit.Total - audit.Missing.Count} 个可解析：{audit.Summary}）");
            AppendLog("   缺失的这些一旦被引用就会抛 FileNotFoundException（那是缺文件，不是 API 本身的问题）。");
            AppendLog("   排查：① 对照完整运行目录把同名 DLL 一并拷进来；");
            AppendLog("         ② 或在「设置 → 额外依赖搜索路径」里指向那个完整目录。");
            AppendLog("   详情见 %TEMP%\\MotionApiTester\\audit.log（四个桶逐条列出）");

            StatusText += $" ⚠️ 缺 {audit.Missing.Count} 个依赖: {string.Join(", ", audit.Missing.Take(3))}"
                        + (audit.Missing.Count > 3 ? " …" : "");
            return audit.Missing.Count;
        }

        /// <summary>
        /// AssemblyResolve 拦截：Costura 嵌入的 DLL 在 LoadFile 上下文内查找外部依赖时，
        /// 交给 DependencyResolver 按"已登记字节 → 搜索路径"解析。
        /// </summary>
        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs e) => _dependencyResolver.Resolve(e.Name);

        /// <summary>检查 DLL 是否为 .NET 程序集（原生 C++ DLL 返回 false）</summary>
        private static bool IsDotNetAssembly(string dllPath)
        {
            try
            {
                AssemblyName.GetAssemblyName(dllPath);
                return true;
            }
            catch
            {
                // BadImageFormatException 即原生 DLL；其余读取失败同样按不可反射处理
                return false;
            }
        }

        /// <summary>切换到选中的设备</summary>
        private void SwitchDevice()
        {
            if (SelectedDevice == null) return;

            var (ok, msg) = _deviceManager.CheckDeviceReady(SelectedDevice);
            if (!ok)
            {
                StatusText = $"❌ 设备未就绪: {msg}";
                return;
            }

            _deviceManager.ActivateDevice(SelectedDevice);
            LoadDeviceFromDirectory(SelectedDevice.Directory);

            // CheckDeviceReady 用 ok=true + "⚠️ 缺少 LTSMC.dll" 表示"能加载但有风险"，
            // 这里不能把警告覆盖掉，否则用户看不到。
            StatusText = $"✅ 已切换到 {SelectedDevice.Name}"
                + (msg.StartsWith("⚠️") ? $" · {msg}" : "");
        }

        /// <summary>添加设备向导</summary>
        private void AddDevice()
        {
            var wizard = new DeviceWizardWindow();
            if (wizard.ShowDialog() != true || wizard.ResultDevice == null) return;

            var requested = wizard.ResultDevice;
            var profile = _deviceManager.AddDevice(requested.Directory, requested.ModelDllName, requested.MachineType);

            // 尊重向导里填写的设备名（AddDevice 默认用目录名）
            if (!string.IsNullOrWhiteSpace(requested.Name) && requested.Name != profile.Name)
            {
                profile.Name = requested.Name;
                _deviceManager.Save();
            }

            _devices.Add(profile);
            SelectedDevice = profile;
            StatusText = $"✅ 已添加设备 {profile.Name}";
        }

        /// <summary>移除选中设备</summary>
        private void RemoveSelectedDevice()
        {
            if (SelectedDevice == null) return;

            var name = SelectedDevice.Name;
            if (MessageBox.Show($"确定要移除设备「{name}」吗？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            _deviceManager.RemoveDevice(SelectedDevice);
            _devices.Remove(SelectedDevice);
            SelectedDevice = null;
            StatusText = $"已移除设备 {name}";
        }

        /// <summary>打开设置窗口</summary>
        private void OpenSettings()
        {
            var win = new SettingsWindow(_settingsService.Settings, this);
            if (win.ShowDialog() != true) return;

            _settingsService.Settings = win.ResultSettings;
            _settingsService.Save();
            _maxLogLines = _settingsService.Settings.LogRetentionLines;
            ThemeMode = _settingsService.Settings.Theme;
            _deviceBinDirCache = null;   // 设备目录可能已改，下次访问重新解析
            StatusText = "设置已保存";
        }

        /// <summary>将加载错误写入 %TEMP%\MotionApiTester\load-error.log（供事后排查）</summary>
        private static void LogLoadError(string context, Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "MotionApiTester", "load-error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}\n{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
            }
            catch { /* 日志失败不应影响加载流程 */ }
        }
    }
}
