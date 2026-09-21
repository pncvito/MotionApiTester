using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MotionApiTester.Models;
using MotionApiTester.Services;
using MotionApiTester.Views;

namespace MotionApiTester.ViewModels
{
    /// <summary>主视图模式</summary>
    public enum ViewMode
    {
        Api,
        Native
    }
    public class MainViewModel : ObservableObject
    {
        // ============== 服务 ==============
        private readonly AssemblyLoader _loader = new AssemblyLoader();
        private readonly ReflectionEnumerator _enumerator = new ReflectionEnumerator();
        private readonly DeviceDirectoryScanner _scanner = new DeviceDirectoryScanner();
        private readonly ApiInvoker _invoker;
        private readonly SettingsService _settingsService;
        private readonly DeviceManager _deviceManager;
        private readonly HistoryService _historyService;
        private readonly NativeDllInspector _nativeInspector = new NativeDllInspector();
        private readonly DispatcherTimer _logFlushTimer;
        private readonly Dictionary<string, byte[]> _assemblyByteCache = new Dictionary<string, byte[]>();
        /// <summary>本次加载的程序集(交给 ApiInvoker 解析接口实现)</summary>
        private readonly List<Assembly> _loadedAssemblies = new List<Assembly>();

        // ============== 私有字段 ==============
        private string _statusText = "就绪 — 请选择设备 DLL 目录";
        private string _machineTypeText = "";
        private ObservableCollection<ApiAssembly> _assemblies = new ObservableCollection<ApiAssembly>();
        private ApiMethod _selectedMethod;
        private string _logText = "";
        private string _resultText = "";
        private bool _isLoaded = false;
        private ObservableCollection<CallHistoryItem> _historyItems = new ObservableCollection<CallHistoryItem>();
        private string _searchText = "";
        private bool _isDarkTheme;
        private DeviceProfile _selectedDevice;
        private ObservableCollection<DeviceProfile> _devices = new ObservableCollection<DeviceProfile>();
        private ObservableCollection<NativeDllInfo> _nativeDlls = new ObservableCollection<NativeDllInfo>();
        private bool _isInvoking;
        private int _maxLogLines = 5000;

        /// <summary>设备 DLL 目录默认值</summary>
        private const string DefaultDeviceBinDir = @"D:\MotionApiTester\Bin";

        /// <summary>当前设备 DLL 目录(settings.json 的 DefaultDeviceDirectory 优先)</summary>
        private string DeviceBinDir
        {
            get
            {
                var configured = _settingsService?.Settings?.DefaultDeviceDirectory;
                return string.IsNullOrWhiteSpace(configured) ? DefaultDeviceBinDir : configured;
            }
        }

        // ============== 属性 ==============
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
        public string MachineTypeText { get => _machineTypeText; set => SetProperty(ref _machineTypeText, value); }
        public ObservableCollection<ApiAssembly> Assemblies { get => _assemblies; set => SetProperty(ref _assemblies, value); }
        public ApiMethod SelectedMethod
        {
            get => _selectedMethod;
            set { if (SetProperty(ref _selectedMethod, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        public string LogText { get => _logText; set => SetProperty(ref _logText, value); }
        public string ResultText { get => _resultText; set => SetProperty(ref _resultText, value); }
        public bool IsLoaded { get => _isLoaded; set => SetProperty(ref _isLoaded, value); }
        public ObservableCollection<CallHistoryItem> HistoryItems { get => _historyItems; set => SetProperty(ref _historyItems, value); }

        /// <summary>过滤后的程序集（用于 TreeView 绑定）</summary>
        public ObservableCollection<TreeNodeVm> FilteredTreeRoots { get; } = new ObservableCollection<TreeNodeVm>();

        /// <summary>TreeView 当前选中节点（可能是 ApiMethod/ApiProperty/ApiField/ApiType）</summary>
        public TreeNodeVm SelectedTreeNode
        {
            get => _selectedTreeNode;
            set
            {
                if (SetProperty(ref _selectedTreeNode, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                    UpdateSelection();
                }
            }
        }
        private TreeNodeVm _selectedTreeNode;

        /// <summary>上次调用耗时(ms) — 显示在操作栏</summary>
        public long LastElapsedMs
        {
            get => _lastElapsedMs;
            set => SetProperty(ref _lastElapsedMs, value);
        }
        private long _lastElapsedMs;

        /// <summary>上次调用时间戳</summary>
        public string LastCallTime
        {
            get => _lastCallTime;
            set => SetProperty(ref _lastCallTime, value);
        }
        private string _lastCallTime = "—";

        // 结构化结果字段(用于 KV 显示)
        public string ResultReturnType { get => _resultReturnType; set => SetProperty(ref _resultReturnType, value); }
        private string _resultReturnType = "—";
        public string ResultReturnValue { get => _resultReturnValue; set => SetProperty(ref _resultReturnValue, value); }
        private string _resultReturnValue = "—";
        public string ResultInstanceType { get => _resultInstanceType; set => SetProperty(ref _resultInstanceType, value); }
        private string _resultInstanceType = "—";
        public string ResultThread { get => _resultThread; set => SetProperty(ref _resultThread, value); }
        private string _resultThread = "Background (Task.Run)";
        public bool ResultSuccessFlag { get => _resultSuccessFlag; set => SetProperty(ref _resultSuccessFlag, value); }
        private bool _resultSuccessFlag;
        public string ResultFullMessage { get => _resultFullMessage; set => SetProperty(ref _resultFullMessage, value); }
        private string _resultFullMessage = "—";

        /// <summary>总类型数(状态栏用)</summary>
        public int TotalTypesCount => Assemblies.Sum(a => a.Types.Count);

        /// <summary>总方法数</summary>
        public int TotalMethodsCount => Assemblies.Sum(a => a.Types.Sum(t => t.Methods.Count + t.Constructors.Count));

        /// <summary>总属性数</summary>
        public int TotalPropertiesCount => Assemblies.Sum(a => a.Types.Sum(t => t.Properties.Count));

        /// <summary>总字段数</summary>
        public int TotalFieldsCount => Assemblies.Sum(a => a.Types.Sum(t => t.Fields.Count));

        /// <summary>原生 DLL 数</summary>
        public int TotalNativeDllsCount => NativeDlls.Count;

        /// <summary>签名文本(绑定显示)</summary>
        public string SignatureText
        {
            get => _signatureText;
            set => SetProperty(ref _signatureText, value);
        }
        private string _signatureText = "";

        /// <summary>当前加载的设备路径(工具栏显示用)</summary>
        public string CurrentLoadedPath
        {
            get => _currentLoadedPath;
            set
            {
                if (SetProperty(ref _currentLoadedPath, value))
                {
                    OnPropertyChanged(nameof(LoadedAssemblySummary));
                }
            }
        }
        private string _currentLoadedPath = "";

        /// <summary>已加载程序集摘要(状态栏用,如 "MyMotionLib.dll (v2.4.1)")</summary>
        public string LoadedAssemblySummary
        {
            get
            {
                if (Assemblies.Count == 0) return "";
                var first = Assemblies[0];
                return $"{first.Name}{(first.Version != null ? $" (v{first.Version})" : "")}";
            }
        }

        /// <summary>当前选中方法的依赖项(用于 "依赖项" Tab)</summary>
        public List<DependencyInfo> MethodDependencies
        {
            get => _methodDependencies;
            set => SetProperty(ref _methodDependencies, value);
        }
        private List<DependencyInfo> _methodDependencies = new List<DependencyInfo>();

        /// <summary>高级 JSON 参数(整组按位置覆盖逐参数输入;切换选中项时自动清空)</summary>
        public string JsonArgsOverride
        {
            get => _jsonArgsOverride;
            set => SetProperty(ref _jsonArgsOverride, value);
        }
        private string _jsonArgsOverride = "";

        /// <summary>API 搜索关键词</summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    RefreshSearch();
            }
        }

        /// <summary>是否深色主题(当前生效值,只读)</summary>
        public bool IsDarkTheme => _isDarkTheme;

        /// <summary>主题模式:Light / Dark / System</summary>
        public string ThemeMode
        {
            get => _themeMode;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "Light" : value;
                if (!SetProperty(ref _themeMode, normalized)) return;

                ApplyTheme();
                _settingsService.Settings.Theme = normalized;
                _settingsService.Save();
            }
        }
        private string _themeMode = "Light";

        /// <summary>已注册的设备列表</summary>
        public ObservableCollection<DeviceProfile> Devices { get => _devices; set => SetProperty(ref _devices, value); }

        /// <summary>当前选中设备</summary>
        public DeviceProfile SelectedDevice
        {
            get => _selectedDevice;
            set { if (SetProperty(ref _selectedDevice, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        /// <summary>原生 DLL 列表</summary>
        public ObservableCollection<NativeDllInfo> NativeDlls { get => _nativeDlls; set => SetProperty(ref _nativeDlls, value); }

        /// <summary>原生 DLL 当前选中（订阅模式面板用）</summary>
        public NativeDllInfo SelectedNativeDll
        {
            get => _selectedNativeDll;
            set
            {
                if (SetProperty(ref _selectedNativeDll, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                    UpdatePInvokeTemplate();
                }
            }
        }
        private NativeDllInfo _selectedNativeDll;

        /// <summary>P/Invoke 模板文本（绑定到右侧文本框）</summary>
        public string PInvokeTemplate
        {
            get => _pInvokeTemplate;
            set => SetProperty(ref _pInvokeTemplate, value);
        }
        private string _pInvokeTemplate = "// ← 选中左侧 DLL 查看 P/Invoke 模板";

        /// <summary>右栏标签页索引(0 = 调用历史, 1 = 原生 DLL)</summary>
        public int RightTabIndex
        {
            get => _rightTabIndex;
            set => SetProperty(ref _rightTabIndex, value);
        }
        private int _rightTabIndex;

        /// <summary>是否存在原生 DLL(空状态提示用)</summary>
        public bool HasNativeDlls => NativeDlls.Count > 0;

        /// <summary>当前选中项是否可调用(方法 / 构造函数 / 属性 / 字段)</summary>
        public bool CanInvokeSelection =>
            SelectedMethod?.Target != null
            || SelectedProperty?.PropertyInfo != null
            || SelectedField?.FieldInfo != null;

        /// <summary>是否正在调用方法（控制进度 UI）</summary>
        public bool IsInvoking
        {
            get => _isInvoking;
            set { if (SetProperty(ref _isInvoking, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        /// <summary>当前激活模式: Api / Native</summary>
        public ViewMode CurrentView
        {
            get => _currentView;
            set => SetProperty(ref _currentView, value);
        }
        private ViewMode _currentView = ViewMode.Api;

        /// <summary>最大日志行数（可在设置中调整）</summary>
        public int MaxLogLines
        {
            get => _maxLogLines;
            set
            {
                if (SetProperty(ref _maxLogLines, value))
                {
                    _settingsService.Settings.LogRetentionLines = value;
                    _settingsService.Save();
                }
            }
        }

        // ============== 命令 ==============
        public ICommand LoadDeviceCommand { get; }
        public ICommand UnloadCommand { get; }
        public ICommand InvokeCommand { get; }
        public ICommand CancelInvokeCommand { get; }
        public ICommand SwitchDeviceCommand { get; }
        public ICommand AddDeviceCommand { get; }
        public ICommand RemoveDeviceCommand { get; }
        public ICommand ToggleThemeCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand ExportLogCommand { get; }
        public ICommand SearchClearCommand { get; }
        public ICommand ShowNativeDllsCommand { get; }
        public ICommand SwitchToApiCommand { get; }
        public ICommand SwitchToNativeCommand { get; }
        public ICommand CopyPInvokeCommand { get; }
        public ICommand RefreshPInvokeCommand { get; }
        public ICommand RefreshNativeDllsCommand { get; }
        public ICommand SearchFocusCommand { get; }

        // ============== 构造 ==============
        public MainViewModel()
        {
            // 服务初始化
            _settingsService = new SettingsService();
            _deviceManager = new DeviceManager();
            _historyService = new HistoryService();
            _invoker = new ApiInvoker(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

            // 安装 AssemblyResolve 处理器:用于解决 Costura 嵌入版冲突
            // BinocRainbow 嵌入的 BaseTester 在 LoadFile 上下文中,
            // 当其内部类型被反射时,CLR 找不到 BaseTester 文件路径版 → FileNotFoundException
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            // 加载已保存的设置
            _themeMode = string.IsNullOrWhiteSpace(_settingsService.Settings.Theme)
                ? "Light" : _settingsService.Settings.Theme;
            _isDarkTheme = ResolveIsDark();
            _maxLogLines = _settingsService.Settings.LogRetentionLines > 0
                ? _settingsService.Settings.LogRetentionLines : 5000;

            // 加载已注册设备
            foreach (var d in _deviceManager.Devices) _devices.Add(d);

            // 加载历史
            foreach (var h in _historyService.Items) _historyItems.Add(h);

            // ApiInvoker 回调 → 异步线程安全（用 ConcurrentQueue）
            _invoker.OnStatusChanged = msg => StatusText = msg;
            _invoker.OnCompleted = (method, result, _) =>
            {
                var item = new CallHistoryItem
                {
                    MethodName = method.FullName,
                    Parameters = string.Join(", ", method.Parameters.Select(p =>
                        p.IsLogger ? "[logger]" : $"{p.Name}={p.Value}")),
                    Result = result.Message,
                    ElapsedMs = result.ElapsedMs,
                    Success = result.Success,
                    Timestamp = DateTime.Now
                };
                _historyService.Record(item);
                _historyItems.Insert(0, item);
                if (_historyItems.Count > 200) _historyItems.RemoveAt(_historyItems.Count - 1);
                _historyService.Save();

                LastElapsedMs = result.ElapsedMs;
                LastCallTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                ResultSuccessFlag = result.Success;

                // 结构化字段
                ResultReturnType = method.IsConstructor
                    ? "(构造函数)"
                    : (method.MethodInfo?.ReturnType.FullName ?? "void");
                ResultReturnValue = result.ReturnValue?.ToString() ?? "(null)";
                ResultInstanceType = method.Target?.DeclaringType?.FullName ?? "—";
                ResultThread = "Background (Task.Run)";
                ResultFullMessage = result.Message;

                ResultText = (result.Success ? "✅ " : "❌ ") +
                    $"{method.Name} ({result.ElapsedMs}ms)\n{result.Message}";
                IsInvoking = false;
            };

            // 日志刷新定时器（避免在后台线程改 INPC）
            _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _logFlushTimer.Tick += (s, e) => FlushLogQueue();
            _logFlushTimer.Start();

            // 命令绑定
            LoadDeviceCommand = new RelayCommand(LoadDevice);
            UnloadCommand = new RelayCommand(UnloadDevice);
            InvokeCommand = new RelayCommand(async () => await InvokeSelectedAsync(), () => CanInvokeSelection && !IsInvoking);
            CancelInvokeCommand = new RelayCommand(() => _invoker.Cancel(), () => IsInvoking);
            SwitchDeviceCommand = new RelayCommand(SwitchDevice, () => SelectedDevice != null);
            AddDeviceCommand = new RelayCommand(AddDevice);
            RemoveDeviceCommand = new RelayCommand(RemoveSelectedDevice, () => SelectedDevice != null);
            ToggleThemeCommand = new RelayCommand(() => ThemeMode = ResolveIsDark() ? "Light" : "Dark");
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            ClearHistoryCommand = new RelayCommand(() =>
            {
                _historyService.Clear();
                _historyItems.Clear();
                StatusText = "已清空调用历史";
            });
            ClearLogCommand = new RelayCommand(() => LogText = "");
            ExportLogCommand = new RelayCommand(ExportLog);
            SearchClearCommand = new RelayCommand(() => SearchText = "");
            ShowNativeDllsCommand = new RelayCommand(() =>
            {
                RightTabIndex = 1;
                CurrentView = ViewMode.Native;
                if (NativeDlls.Count == 0) StatusText = "当前设备目录下未发现原生 DLL（LTSMC.dll 等）";
            });
            SwitchToApiCommand = new RelayCommand(() => CurrentView = ViewMode.Api);
            SwitchToNativeCommand = new RelayCommand(() => CurrentView = ViewMode.Native);
            CopyPInvokeCommand = new RelayCommand(CopyPInvoke);
            RefreshPInvokeCommand = new RelayCommand(RefreshPInvoke);
            RefreshNativeDllsCommand = new RelayCommand(RefreshNativeDlls);
            SearchFocusCommand = new RelayCommand(SearchFocus);

            // 应用主题(窗口还没创建时延后到 Loaded,否则读不到 MainWindow)
            ApplyThemeDeferred();

            // 启动时自动加载
            try
            {
                LoadDeviceFromDirectory(DeviceBinDir);
                // 加载成功后,把当前 Bin 加入"已激活设备",便于 UI 状态栏显示
                if (IsLoaded && Directory.Exists(DeviceBinDir))
                {
                    var mt = MachineTypeReader.Read();
                    var currentDevice = new DeviceProfile
                    {
                        Name = "(当前) " + new DirectoryInfo(DeviceBinDir).Name,
                        Directory = DeviceBinDir,
                        ModelDllName = MachineTypeMatcher.FindBestMatch(mt ?? "", _scanner.Scan(DeviceBinDir).CandidateModelDlls) ?? "",
                        MachineType = mt ?? ""
                    };
                    // 仅在 Devices 中没有相同目录时才添加
                    if (!_devices.Any(d => string.Equals(d.Directory, currentDevice.Directory, StringComparison.OrdinalIgnoreCase)))
                    {
                        _devices.Add(currentDevice);
                        SelectedDevice = currentDevice;
                    }
                }
            }
            catch (Exception ex)
            {
                LogLoadError("Auto-load", ex);
                StatusText = $"❌ 自动加载失败: {ex.GetType().Name}: {ex.Message}";
            }
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
            catch { }
        }

        // ============== 业务方法 ==============

        /// <summary>卸载设备
        /// 说明:程序集一旦载入 AppDomain 就无法真正卸载,这里只清空界面状态与缓存,
        /// 保证重新加载时不会复用旧实例。</summary>
        private void UnloadDevice()
        {
            Assemblies.Clear();
            SelectedMethod = null;
            SelectedProperty = null;
            SelectedField = null;
            ResultText = "";
            ResultFullMessage = "—";
            ResultSuccessFlag = false;
            IsLoaded = false;
            NativeDlls.Clear();
            SelectedNativeDll = null;
            PInvokeTemplate = "// ← 选中左侧 DLL 查看 P/Invoke 模板";
            JsonArgsOverride = "";
            _loadedAssemblies.Clear();
            _assemblyByteCache.Clear();
            _invoker.ClearInstances();
            OnPropertyChanged(nameof(HasNativeDlls));

            StatusText = "已卸载 — 界面已清空(程序集无法从进程卸载，重新加载即可)";
        }

        /// <summary>加载设备按钮命令 —— 从 Bin 目录加载</summary>
        private void LoadDevice()
        {
            LoadDeviceFromDirectory(DeviceBinDir);
        }

        /// <summary>从目录加载设备</summary>
        public void LoadDeviceFromDirectory(string deviceDir)
        {
            Assemblies.Clear();
            NativeDlls.Clear();
            SelectedMethod = null;
            CurrentLoadedPath = deviceDir;
            StatusText = $"🔍 扫描目录: {deviceDir}";

            // ① 读 MachineType.json
            var machineType = MachineTypeReader.Read();
            MachineTypeText = machineType ?? "(未找到 MachineType.json)";

            // ② 扫描设备目录
            var scanResult = _scanner.Scan(deviceDir);
            StatusText = $"📂 目录: {deviceDir} | DLL: {scanResult.AllFiles.Count(d => d.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))} 个 | 候选机型: {scanResult.CandidateModelDlls.Count} 个";

            if (scanResult.CandidateModelDlls.Count == 0)
            {
                StatusText = $"❌ 目录中未找到 OptoFidelity.*.dll（设备机型 DLL）";
                return;
            }

            // ③ 匹配机型 DLL
            string modelDll = null;
            if (machineType != null && scanResult.CandidateModelDlls.Count > 0)
            {
                modelDll = MachineTypeMatcher.FindBestMatch(machineType, scanResult.CandidateModelDlls);
            }
            else if (scanResult.CandidateModelDlls.Count == 1)
            {
                modelDll = scanResult.CandidateModelDlls[0];
            }

            if (modelDll == null)
            {
                StatusText = $"❌ 无法匹配机型 DLL。MachineType={machineType ?? "(无)"}，候选: {string.Join(", ", scanResult.CandidateModelDlls)}";
                return;
            }

            // ④ 加载 DLL
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

            // 检查是否为 .NET 程序集（原生 DLL 无法反射）
            if (!IsDotNetAssembly(baseDll))
            {
                StatusText = $"⚠️ BaseTester.dll 是原生 C++ DLL，无法反射枚举 API";
                return;
            }
            if (!IsDotNetAssembly(modelPath))
            {
                StatusText = $"⚠️ {modelDll} 是原生 C++ DLL，无法反射枚举 API";
                return;
            }

            StatusText = $"⏳ 加载 {modelDll}...";

            // 先读取两个 DLL 字节并注册到 AssemblyResolve 缓存
            // 这样后续 BinocRainbow 内部类型查找 BaseTester 时可以返回正确的字节
            var baseBytes = File.Exists(baseDll) ? File.ReadAllBytes(baseDll) : null;
            var modelBytes = File.Exists(modelPath) ? File.ReadAllBytes(modelPath) : null;

            if (baseBytes != null)
            {
                var baseAsm = System.Reflection.Assembly.Load(baseBytes);
                _assemblyByteCache[baseAsm.GetName().Name] = baseBytes;
            }
            if (modelBytes != null)
            {
                var modelAsm = System.Reflection.Assembly.Load(modelBytes);
                _assemblyByteCache[modelAsm.GetName().Name] = modelBytes;
            }

            // ⑤ 反射枚举(从 byte[] 加载)
            _loadedAssemblies.Clear();
            try
            {
                if (baseBytes != null)
                {
                    var asm = System.Reflection.Assembly.Load(baseBytes);
                    _loadedAssemblies.Add(asm);
                    var api = _enumerator.Enumerate(asm, baseDll);
                    Assemblies.Add(api);
                }

                if (modelBytes != null)
                {
                    var asm = System.Reflection.Assembly.Load(modelBytes);
                    _loadedAssemblies.Add(asm);
                    var api = _enumerator.Enumerate(asm, modelPath);
                    Assemblies.Add(api);
                }
            }
            catch (BadImageFormatException ex)
            {
                LogLoadError("LoadDLL: BadImage", ex);
                StatusText = $"❌ DLL 格式错误（不是有效的 .NET 程序集）: {ex.Message}";
                return;
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 部分类型加载失败 — 已经从 ex.Types 拿到能加载的类型,继续展示
                LogLoadError("LoadDLL: ReflectionType", ex);
                StatusText = $"⚠️ 部分类型加载失败: {ex.LoaderExceptions?.Length ?? 0} 个错误（已尽可能展示）";
            }
            catch (Exception ex)
            {
                LogLoadError("LoadDLL", ex);
                StatusText = $"❌ 加载失败: {ex.GetType().Name}: {ex.Message}";
                return;
            }

            // 把本次加载的程序集交给调用器,用于解析接口 / 抽象类型的具体实现
            _invoker.ClearInstances();
            _invoker.SetCandidateAssemblies(_loadedAssemblies);

            // ⑥ 状态栏
            int typeCount = Assemblies.Sum(a => a.Types.Count);
            int methodCount = Assemblies.Sum(a => a.Types.Sum(t => t.Methods.Count));
            int loggerCount = Assemblies.Sum(a => a.Types.Sum(t => t.Methods.Count(m => m.HasLoggerParameter)));
            int embedded = Assemblies.Sum(a => a.EmbeddedCount);
            var errors = Assemblies.SelectMany(a => a.LoadErrors).ToList();

            StatusText = $"✅ {modelDll} | 🔷 {typeCount} 类型 ⚙ {methodCount} 方法 📋 {loggerCount} logger 📦 {embedded} 嵌入";
            if (errors.Any())
                StatusText += $" ⚠️ {errors.Count} 个加载错误";
            IsLoaded = true;

            // 检查 LTSMC
            if (!scanResult.HasLtSmc)
                StatusText += " ⚠️ 缺 LTSMC.dll";

            // ⑦ 扫描原生 DLL
            ScanNativeDlls(deviceDir);

            // ⑧ 应用搜索过滤
            RefreshSearch();
        }

        /// <summary>调用当前选中的方法 / 构造函数 / 属性 / 字段</summary>
        private async System.Threading.Tasks.Task InvokeSelectedAsync()
        {
            // 1. 方法 与 构造函数(统一走 Target 入口)
            var method = SelectedMethod;
            if (method?.Target != null)
            {
                IsInvoking = true;
                try { await _invoker.InvokeAsync(method, JsonArgsOverride); }
                catch (Exception ex) { StatusText = $"❌ 调用异常: {ex.Message}"; }
                finally { IsInvoking = false; }
                return;
            }

            // 2. 属性(get)
            var prop = SelectedProperty;
            if (prop?.PropertyInfo != null)
            {
                if (!prop.CanRead)
                {
                    StatusText = $"⚠️ 属性 {prop.Name} 为只写，无法读取";
                    return;
                }

                IsInvoking = true;
                try
                {
                    var pi = prop.PropertyInfo;
                    var value = await ReadMemberAsync(pi.DeclaringType, prop.IsStatic, instance => pi.GetValue(instance));

                    ResultSuccessFlag = true;
                    ResultReturnType = pi.PropertyType.FullName;
                    ResultReturnValue = value?.ToString() ?? "(null)";
                    ResultInstanceType = pi.DeclaringType.FullName;
                    ResultThread = "Background (Task.Run)";
                    ResultFullMessage = $"属性 {prop.Name} = {value?.ToString() ?? "(null)"}";
                    ResultText = $"✅ 属性 {prop.Name} ({LastElapsedMs}ms)\n{ResultFullMessage}";
                    StatusText = $"✅ {prop.Name} = {value}";
                }
                catch (Exception ex) { ReportMemberReadFailure("属性", prop.Name, ex); }
                finally { IsInvoking = false; }
                return;
            }

            // 3. 字段(get)
            var field = SelectedField;
            if (field?.FieldInfo != null)
            {
                IsInvoking = true;
                try
                {
                    var fi = field.FieldInfo;
                    var value = await ReadMemberAsync(fi.DeclaringType, field.IsStatic, instance => fi.GetValue(instance));

                    ResultSuccessFlag = true;
                    ResultReturnType = fi.FieldType.FullName;
                    ResultReturnValue = value?.ToString() ?? "(null)";
                    ResultInstanceType = fi.DeclaringType.FullName;
                    ResultThread = "Background (Task.Run)";
                    ResultFullMessage = $"字段 {field.Name} = {value?.ToString() ?? "(null)"}";
                    ResultText = $"✅ 字段 {field.Name} ({LastElapsedMs}ms)\n{ResultFullMessage}";
                    StatusText = $"✅ {field.Name} = {value}";
                }
                catch (Exception ex) { ReportMemberReadFailure("字段", field.Name, ex); }
                finally { IsInvoking = false; }
                return;
            }

            StatusText = "❌ 未选中可调用项（请选择方法 / 构造函数 / 属性 / 字段）";
        }

        /// <summary>读取属性 / 字段的公共路径:后台线程 + 接口实现解析 + 统一计时</summary>
        private async System.Threading.Tasks.Task<object> ReadMemberAsync(Type declaringType, bool isStatic, Func<object, object> read)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var value = await System.Threading.Tasks.Task.Run(() =>
            {
                var instance = isStatic ? null : _invoker.ResolveInstance(declaringType);
                return read(instance);
            });
            sw.Stop();

            LastElapsedMs = sw.ElapsedMilliseconds;
            LastCallTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            return value;
        }

        /// <summary>属性 / 字段读取失败时的统一反馈</summary>
        private void ReportMemberReadFailure(string kind, string name, Exception ex)
        {
            var inner = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;

            ResultSuccessFlag = false;
            ResultFullMessage = $"{inner.GetType().Name}: {inner.Message}";
            ResultText = $"❌ {kind} {name}\n{ResultFullMessage}";
            StatusText = $"❌ {kind}读取异常: {inner.Message}";
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

            // CheckDeviceReady 用 ok=true + "⚠️ 缺少 LTSMC.dll" 表示"能加载但有风险",
            // 这里不能把警告覆盖掉,否则用户看不到。
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

            // 尊重向导里填写的设备名(AddDevice 默认用目录名)
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
            var win = new SettingsWindow(_settingsService.Settings);
            if (win.ShowDialog() == true)
            {
                _settingsService.Settings = win.ResultSettings;
                _settingsService.Save();
                _maxLogLines = _settingsService.Settings.LogRetentionLines;
                ThemeMode = _settingsService.Settings.Theme;
                StatusText = "设置已保存";
            }
        }

        /// <summary>扫描原生 DLL</summary>
        private void ScanNativeDlls(string directory)
        {
            NativeDlls.Clear();
            SelectedNativeDll = null;
            try
            {
                if (!Directory.Exists(directory)) return;

                // 角色识别只需扫一次目录,不要放进循环里重复扫描
                var roles = _scanner.Scan(directory).DllRoles;

                foreach (var dll in Directory.GetFiles(directory, "*.dll"))
                {
                    var name = Path.GetFileName(dll);
                    if (IsDotNetAssembly(dll)) continue; // 跳过 .NET DLL
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
                    SelectedNativeDll = NativeDlls[0]; // 自动选中首个,直接出 P/Invoke 模板
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

        /// <summary>显示原生 DLL 详情</summary>
        private void ShowNativeDlls()
        {
            CurrentView = ViewMode.Native;
            if (NativeDlls.Count == 0)
            {
                StatusText = "当前未加载原生 DLL。";
            }
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

        /// <summary>供 UI 调用的同步方法</summary>
        public string GeneratePInvokeTemplateForSelected() => PInvokeTemplate;

        /// <summary>UI 调用的刷新入口</summary>
        public void RefreshPInvokeTemplate() => UpdatePInvokeTemplate();

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

        private void RefreshPInvoke() => UpdatePInvokeTemplate();

        /// <summary>导出日志</summary>
        private void ExportLog()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "文本文件|*.txt|所有文件|*.*",
                FileName = $"MotionApiTester-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, LogText);
                    StatusText = $"✅ 日志已导出: {dlg.FileName}";
                }
                catch (Exception ex)
                {
                    StatusText = $"❌ 导出失败: {ex.Message}";
                }
            }
        }

        /// <summary>刷新搜索（按 SearchText 真正过滤 FilteredTreeRoots）</summary>
        private void RefreshSearch()
        {
            FilteredTreeRoots.Clear();

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // 无搜索 → 显示全部
                foreach (var node in BuildAllTrees()) FilteredTreeRoots.Add(node);
                if (Assemblies.Count > 0 && StatusText.StartsWith("🔍"))
                    StatusText = "✅ 就绪";
                return;
            }

            int matchCount = 0;
            foreach (var root in BuildAllTrees())
            {
                var pruned = PruneTree(root, ref matchCount);
                if (pruned != null) FilteredTreeRoots.Add(pruned);
            }

            StatusText = $"🔍 \"{_searchText}\" → {matchCount} 个匹配";
        }

        /// <summary>从 Assemblies 构建完整的 TreeNodeVm 列表</summary>
        private IEnumerable<TreeNodeVm> BuildAllTrees()
        {
            foreach (var asm in Assemblies)
                yield return BuildTreeForAssembly(asm, isSearchFilter: false, searchKeyword: null);
        }

        /// <summary>构建单个程序集的树</summary>
        private TreeNodeVm BuildTreeForAssembly(ApiAssembly asm, bool isSearchFilter, string searchKeyword)
        {
            var asmNode = new TreeNodeVm
            {
                Label = System.IO.Path.GetFileName(asm.Path) + (asm.Version != null ? $" v{asm.Version}" : ""),
                Icon = "⊞",
                IconColor = "#0078D4",
                NodeKind = "Assembly",
                Badge = asm.IsCostura ? $"📦 Costura · {asm.EmbeddedCount} 嵌入" : "",
                Payload = asm
            };

            // 按命名空间分组
            var byNamespace = asm.Types
                .GroupBy(t => string.IsNullOrEmpty(t.Namespace) ? "(全局)" : t.Namespace)
                .OrderBy(g => g.Key);

            foreach (var nsGroup in byNamespace)
            {
                TreeNodeVm nsNode;
                if (nsGroup.Key == "(全局)")
                {
                    nsNode = asmNode; // 直接挂在 Assembly 下
                }
                else
                {
                    nsNode = new TreeNodeVm
                    {
                        Label = nsGroup.Key,
                        Icon = "📦",
                        IconColor = "#737373",
                        NodeKind = "Namespace"
                    };
                    asmNode.Children.Add(nsNode);
                }

                foreach (var t in nsGroup)
                {
                    var typeNode = BuildTypeNode(t, isSearchFilter, searchKeyword);
                    if (typeNode != null) nsNode.Children.Add(typeNode);
                }
            }

            return asmNode;
        }

        /// <summary>构建类型节点(含构造/方法/属性/字段分组)</summary>
        private TreeNodeVm BuildTypeNode(ApiType t, bool isSearchFilter, string searchKeyword)
        {
            var typeNode = new TreeNodeVm
            {
                Label = t.Name,
                Icon = TypeIcon(t.Kind),
                IconColor = TypeColor(t.Kind),
                NodeKind = "Type",
                Badge = t.Kind,
                Payload = t
            };

            // 构造函数分组
            if (t.Constructors.Count > 0)
            {
                var grp = new TreeNodeVm
                {
                    Label = "构造函数",
                    Icon = "🔷",
                    IconColor = "#0078D4",
                    NodeKind = "Group"
                };
                foreach (var ctor in t.Constructors)
                {
                    bool match = isSearchFilter && (Contains(ctor.Signature) || Contains(ctor.Name));
                    if (!isSearchFilter || match)
                    {
                        grp.Children.Add(new TreeNodeVm
                        {
                            Label = ctor.Signature,
                            Icon = "🔷",
                            IconColor = "#0078D4",
                            NodeKind = "Constructor",
                            Payload = ctor
                        });
                    }
                }
                typeNode.Children.Add(grp);
            }

            // 方法分组
            if (t.Methods.Count > 0)
            {
                var grp = new TreeNodeVm
                {
                    Label = $"方法 ({t.Methods.Count})",
                    Icon = "⚙",
                    IconColor = "#CA5010",
                    NodeKind = "Group"
                };
                foreach (var m in t.Methods)
                {
                    bool match = isSearchFilter && (Contains(m.Signature) || Contains(m.Name));
                    if (!isSearchFilter || match)
                    {
                        grp.Children.Add(new TreeNodeVm
                        {
                            Label = m.Signature,
                            Icon = m.IsStatic ? "⚡" : "⚙",
                            IconColor = m.IsStatic ? "#CA5010" : "#0078D4",
                            NodeKind = m.IsStatic ? "StaticMethod" : "InstanceMethod",
                            Payload = m
                        });
                    }
                }
                typeNode.Children.Add(grp);
            }

            // 属性分组
            if (t.Properties.Count > 0)
            {
                var grp = new TreeNodeVm
                {
                    Label = $"属性 ({t.Properties.Count})",
                    Icon = "🔮",
                    IconColor = "#8764B8",
                    NodeKind = "Group"
                };
                foreach (var p in t.Properties)
                {
                    bool match = isSearchFilter && (Contains(p.Name) || Contains(p.Signature));
                    if (!isSearchFilter || match)
                    {
                        grp.Children.Add(new TreeNodeVm
                        {
                            Label = p.Signature,
                            Icon = "🔮",
                            IconColor = "#8764B8",
                            NodeKind = "Property",
                            Payload = p
                        });
                    }
                }
                typeNode.Children.Add(grp);
            }

            // 字段分组
            if (t.Fields.Count > 0)
            {
                var grp = new TreeNodeVm
                {
                    Label = $"字段 ({t.Fields.Count})",
                    Icon = "▣",
                    IconColor = "#737373",
                    NodeKind = "Group"
                };
                foreach (var f in t.Fields)
                {
                    bool match = isSearchFilter && (Contains(f.Name) || Contains(f.Signature));
                    if (!isSearchFilter || match)
                    {
                        grp.Children.Add(new TreeNodeVm
                        {
                            Label = f.Signature,
                            Icon = "▣",
                            IconColor = "#737373",
                            NodeKind = "Field",
                            Payload = f
                        });
                    }
                }
                typeNode.Children.Add(grp);
            }

            // 过滤:若搜索时类型/分组/方法全部为空,返回 null(上层会跳过)
            if (isSearchFilter && typeNode.Children.Count == 0)
            {
                if (!Contains(t.Name) && !Contains(t.Namespace))
                    return null;
            }

            return typeNode;
        }

        /// <summary>过滤模式下,递归移除不匹配的空子树</summary>
        private TreeNodeVm PruneTree(TreeNodeVm node, ref int matchCount)
        {
            var prunedChildren = new List<TreeNodeVm>();
            foreach (var child in node.Children)
            {
                var pruned = PruneTree(child, ref matchCount);
                if (pruned != null) prunedChildren.Add(pruned);
            }
            node.Children.Clear();
            foreach (var c in prunedChildren) node.Children.Add(c);

            // 叶子节点(实际成员):若匹配或被父级需要保留
            if (node.NodeKind == "Method" || node.NodeKind == "StaticMethod" || node.NodeKind == "InstanceMethod"
                || node.NodeKind == "Constructor" || node.NodeKind == "Property" || node.NodeKind == "Field")
            {
                if (Contains(node.Label))
                {
                    matchCount++;
                    return node;
                }
                return null;
            }

            // 中间节点:有子节点 OR 节点本身匹配
            if (node.Children.Count > 0 || Contains(node.Label))
            {
                return node;
            }
            return null;
        }

        private static string TypeIcon(string kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case "interface": return "🟩";
                case "struct": return "🟦";
                case "enum": return "🟧";
                default: return "🟦"; // class
            }
        }

        private static string TypeColor(string kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case "interface": return "#107C10";
                case "struct": return "#2B88D8";
                case "enum": return "#CA5010";
                default: return "#0078D4"; // class
            }
        }

        /// <summary>选中节点后,更新 SelectedMethod / SelectedProperty / SelectedField 等</summary>
        private void UpdateSelection()
        {
            var node = _selectedTreeNode;
            SelectedMethod = node?.Method;
            SelectedProperty = node?.Property;
            SelectedField = node?.Field;

            // 换了选中项，上一组 JSON 参数不再适用，清空避免误调用
            JsonArgsOverride = "";

            // 更新签名文本
            if (node?.Method != null) SignatureText = node.Method.Signature;
            else if (node?.Property != null) SignatureText = node.Property.Signature;
            else if (node?.Field != null) SignatureText = node.Field.Signature;
            else SignatureText = "";

            // 填充"依赖项" Tab
            var deps = new List<DependencyInfo>();
            if (node?.Method?.Target != null)
            {
                var target = node.Method.Target;

                if (node.Method.IsConstructor)
                    deps.Add(new DependencyInfo { Kind = "构造类型", Name = target.DeclaringType?.FullName ?? "—" });
                else if (target is MethodInfo mi)
                    deps.Add(new DependencyInfo { Kind = "返回类型", Name = mi.ReturnType.FullName });

                foreach (var p in target.GetParameters())
                {
                    deps.Add(new DependencyInfo { Kind = p.IsOut ? "参数(out)" : "参数", Name = $"{p.ParameterType.FullName} {p.Name}" });
                }
            }
            else if (node?.Property?.PropertyInfo != null)
            {
                deps.Add(new DependencyInfo { Kind = "属性类型", Name = node.Property.PropertyInfo.PropertyType.FullName });
                deps.Add(new DependencyInfo { Kind = "声明类型", Name = node.Property.PropertyInfo.DeclaringType.FullName });
            }
            else if (node?.Field?.FieldInfo != null)
            {
                deps.Add(new DependencyInfo { Kind = "字段类型", Name = node.Field.FieldInfo.FieldType.FullName });
                deps.Add(new DependencyInfo { Kind = "声明类型", Name = node.Field.FieldInfo.DeclaringType.FullName });
            }
            MethodDependencies = deps;

            // 若选中类型节点
            if (node?.NodeKind == "Type" && node.Payload is ApiType t)
            {
                ResultText = $"类型: {t.FullName}\n命名空间: {t.Namespace}\n种类: {t.Kind}\n成员数: 方法 {t.Methods.Count}, 属性 {t.Properties.Count}, 字段 {t.Fields.Count}, 构造函数 {t.Constructors.Count}";
            }
        }

        /// <summary>聚焦搜索框(由 Ctrl+F 调用)</summary>
        private void SearchFocus()
        {
            if (Application.Current?.MainWindow is MainWindow mw)
                mw.FocusSearchBox();
        }

        /// <summary>
        /// AssemblyResolve 拦截:当 Costura 嵌入的 DLL 在 LoadFile 上下文内查找外部依赖时,
        /// 返回我们缓存的字节。例:BinocRainbow 嵌入 BaseTester,但内部类型反射时
        /// CLR 找不到 BaseTester 文件路径版 → 这里返回 Load(byte[]) 用的字节。
        /// </summary>
        private static readonly string[] _extraSearchPaths = new[]
        {
            @"D:\MotionApiTester\Bin",
            @"D:\Motion\EXE\OptoMotionLib\src\Bin",
            @"D:\Motion\EXE\OptoMotionLib\src\Bin_Library\motion",
            @"C:\Users\19565\.nuget\packages",
            System.IO.Path.GetTempPath() + "MotionApiTester"
        };

        private System.Reflection.Assembly OnAssemblyResolve(object sender, ResolveEventArgs e)
        {
            try
            {
                // e.Name 可能是 "OptoFidelity.BaseTester" 或 "OptoFidelity.BaseTester, Version=..."
                var simpleName = e.Name?.Split(',')[0].Trim();
                if (string.IsNullOrEmpty(simpleName)) return null;

                // 1. 用户加载过的 DLL 字节缓存
                if (_assemblyByteCache.TryGetValue(simpleName, out var bytes))
                {
                    return System.Reflection.Assembly.Load(bytes);
                }

                // 2. Bin 目录 + 多个额外搜索路径
                foreach (var dir in _extraSearchPaths)
                {
                    if (!Directory.Exists(dir)) continue;

                    // 直接 .dll 文件
                    var direct = Path.Combine(dir, simpleName + ".dll");
                    if (File.Exists(direct))
                        return System.Reflection.Assembly.LoadFrom(direct);

                    // NuGet 风格子目录(包名/版本/lib/netXXX/)
                    var nugetDirs = Directory.GetDirectories(dir, simpleName + "*", SearchOption.TopDirectoryOnly);
                    foreach (var nd in nugetDirs)
                    {
                        var dll = Directory.GetFiles(nd, simpleName + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                        if (dll != null && File.Exists(dll))
                            return System.Reflection.Assembly.LoadFrom(dll);
                    }
                }
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MotionApiTester", "resolve-errors.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {e.Name}: {ex.Message}\n");
            }
            return null;
        }

        public ApiProperty SelectedProperty
        {
            get => _selectedProperty;
            set { if (SetProperty(ref _selectedProperty, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        private ApiProperty _selectedProperty;

        public ApiField SelectedField
        {
            get => _selectedField;
            set { if (SetProperty(ref _selectedField, value)) CommandManager.InvalidateRequerySuggested(); }
        }
        private ApiField _selectedField;

        public string TypeFullName { get; set; }

        private bool Contains(string s) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>刷新日志队列 → LogText</summary>
        private void FlushLogQueue()
        {
            var appended = new System.Text.StringBuilder();
            while (_invoker.TryDequeueLog(out var msg))
                appended.AppendLine(msg);

            if (appended.Length == 0) return;

            LogText += appended.ToString();

            // 截断
            var lines = LogText.Split('\n');
            if (lines.Length > _maxLogLines)
            {
                LogText = string.Join("\n", lines.Skip(lines.Length - _maxLogLines));
            }
        }

        /// <summary>应用主题</summary>
        private void ApplyTheme()
        {
            var dark = ResolveIsDark();
            _isDarkTheme = dark;
            OnPropertyChanged(nameof(IsDarkTheme));

            var dictPath = dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
            var dict = new Uri(dictPath, UriKind.Relative);

            // 替换主窗口资源
            if (Application.Current?.MainWindow != null)
            {
                var merged = Application.Current.MainWindow.Resources.MergedDictionaries;
                // 移除旧主题
                for (int i = merged.Count - 1; i >= 0; i--)
                {
                    var src = merged[i].Source?.ToString();
                    if (src != null && (src.Contains("LightTheme") || src.Contains("DarkTheme")))
                        merged.RemoveAt(i);
                }
                merged.Add(new ResourceDictionary { Source = dict });
            }
        }

        /// <summary>
        /// 启动时机问题:MainViewModel 由 MainWindow 的 XAML 在窗口构造函数中创建,
        /// 此时 Application.Current.MainWindow 仍为 null,直接应用主题会被静默跳过,
        /// 表现为"设置里存了深色主题但启动还是浅色"。因此延后到窗口 Loaded 之后再应用。
        /// </summary>
        private void ApplyThemeDeferred()
        {
            if (Application.Current == null)
            {
                ApplyTheme();
                return;
            }

            if (Application.Current.MainWindow == null)
                Application.Current.Dispatcher.BeginInvoke(new Action(ApplyTheme), DispatcherPriority.Loaded);
            else
                ApplyTheme();
        }

        /// <summary>主题模式 → 是否深色(跟随系统时读注册表)</summary>
        private bool ResolveIsDark()
        {
            if (string.Equals(_themeMode, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(_themeMode, "Light", StringComparison.OrdinalIgnoreCase)) return false;
            return IsOsDarkTheme();
        }

        /// <summary>读取系统"应用"主题是否为深色</summary>
        private static bool IsOsDarkTheme()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    if (value is int light) return light == 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>检查 DLL 是否为 .NET 程序集（原生 C++ DLL 返回 false）</summary>
        private static bool IsDotNetAssembly(string dllPath)
        {
            try
            {
                AssemblyName.GetAssemblyName(dllPath);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>复制 DLL 到临时目录，避免文件锁定</summary>
        private static string CopyToTemp(string dllPath)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "MotionApiTester");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}_{Path.GetFileName(dllPath)}");
            File.Copy(dllPath, tempPath, overwrite: true);
            return tempPath;
        }
    }

    /// <summary>简单的 ICommand 实现</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();

        public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>异步命令（async/await）</summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<System.Threading.Tasks.Task> _execute;
        private readonly Func<bool> _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<System.Threading.Tasks.Task> execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) =>
            !_isExecuting && (_canExecute?.Invoke() ?? true);

        public async void Execute(object parameter)
        {
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute();
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}