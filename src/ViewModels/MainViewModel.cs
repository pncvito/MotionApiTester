using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// <summary>
    /// 主窗口 ViewModel。
    ///
    /// 本类按职责拆成多个 partial 文件：
    ///   MainViewModel.cs              —— 服务/字段/属性/命令声明 + 构造装配
    ///   MainViewModel.Device.cs       —— 设备目录加载、卸载、设备 CRUD、设置窗口
    ///   MainViewModel.Invocation.cs   —— 方法/构造函数/属性/字段的调用与选中态
    ///   MainViewModel.Tree.cs         —— 搜索过滤与树数据接线
    ///   MainViewModel.NativeDll.cs    —— 原生 DLL 扫描与 P/Invoke 模板
    ///   MainViewModel.Theme.cs        —— 主题模式与生效状态
    ///   MainViewModel.Log.cs          —— 日志缓冲刷新与导出
    ///
    /// 独立的算法/基础设施逻辑已抽到服务层，不在本类内：
    ///   TreeBuilder 树构建、ThemeManager 主题、DependencyResolver 依赖解析、
    ///   LogTextBuffer 日志缓冲、DeviceDirectoryResolver 目录解析。
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        // ============== 服务 ==============
        private readonly ReflectionEnumerator _enumerator = new ReflectionEnumerator();
        private readonly DeviceDirectoryScanner _scanner = new DeviceDirectoryScanner();
        private readonly NativeDllInspector _nativeInspector = new NativeDllInspector();
        private readonly TreeBuilder _treeBuilder = new TreeBuilder();
        private readonly DependencyResolver _dependencyResolver = new DependencyResolver();
        private readonly LogTextBuffer _logBuffer = new LogTextBuffer();
        private readonly ApiInvoker _invoker;
        private readonly SettingsService _settingsService;
        private readonly DeviceManager _deviceManager;
        private readonly HistoryService _historyService;
        private readonly ThemeManager _themeManager;
        private readonly DispatcherTimer _logFlushTimer;

        /// <summary>本次加载的程序集(交给 ApiInvoker 解析接口实现)</summary>
        private readonly List<Assembly> _loadedAssemblies = new List<Assembly>();

        // ============== 私有字段 ==============
        private string _statusText = "就绪 — 请选择设备 DLL 目录";
        private string _machineTypeText = "";
        private ObservableCollection<ApiAssembly> _assemblies = new ObservableCollection<ApiAssembly>();
        private ApiMethod _selectedMethod;
        private ApiProperty _selectedProperty;
        private ApiField _selectedField;
        private TreeNodeVm _selectedTreeNode;
        private string _logText = "";
        private string _resultText = "";
        private bool _isLoaded = false;
        private ObservableCollection<CallHistoryItem> _historyItems = new ObservableCollection<CallHistoryItem>();
        private string _searchText = "";
        private bool _isDarkTheme;
        private string _themeMode = "Light";
        private DeviceProfile _selectedDevice;
        private ObservableCollection<DeviceProfile> _devices = new ObservableCollection<DeviceProfile>();
        private ObservableCollection<NativeDllInfo> _nativeDlls = new ObservableCollection<NativeDllInfo>();
        private NativeDllInfo _selectedNativeDll;
        private string _pInvokeTemplate = "// ← 选中左侧 DLL 查看 P/Invoke 模板";
        private int _rightTabIndex;
        private bool _isInvoking;
        private int _maxLogLines = 5000;
        private long _lastElapsedMs;
        private string _lastCallTime = "—";
        private string _resultReturnType = "—";
        private string _resultReturnValue = "—";
        private string _resultInstanceType = "—";
        private string _resultThread = "Background (Task.Run)";
        private bool _resultSuccessFlag;
        private string _resultFullMessage = "—";
        private string _signatureText = "";
        private string _currentLoadedPath = "";
        private List<DependencyInfo> _methodDependencies = new List<DependencyInfo>();
        private string _jsonArgsOverride = "";

        /// <summary>已解析的设备 DLL 目录缓存(设置变更时置空重算)</summary>
        private string _deviceBinDirCache;

        // ============== 属性 ==============
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        /// <summary>机型(来自 ConfigHardware\MachineType.json)。底部状态栏"当前设备"摘要也用它。</summary>
        public string MachineTypeText
        {
            get => _machineTypeText;
            set { if (SetProperty(ref _machineTypeText, value)) OnPropertyChanged(nameof(LoadedAssemblySummary)); }
        }
        public ObservableCollection<ApiAssembly> Assemblies { get => _assemblies; set => SetProperty(ref _assemblies, value); }
        public string LogText { get => _logText; set => SetProperty(ref _logText, value); }
        public string ResultText { get => _resultText; set => SetProperty(ref _resultText, value); }
        public bool IsLoaded { get => _isLoaded; set => SetProperty(ref _isLoaded, value); }
        public ObservableCollection<CallHistoryItem> HistoryItems { get => _historyItems; set => SetProperty(ref _historyItems, value); }

        public ApiMethod SelectedMethod
        {
            get => _selectedMethod;
            set { if (SetProperty(ref _selectedMethod, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        public ApiProperty SelectedProperty
        {
            get => _selectedProperty;
            set { if (SetProperty(ref _selectedProperty, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        public ApiField SelectedField
        {
            get => _selectedField;
            set { if (SetProperty(ref _selectedField, value)) CommandManager.InvalidateRequerySuggested(); }
        }

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

        /// <summary>上次调用耗时(ms) — 显示在操作栏</summary>
        public long LastElapsedMs { get => _lastElapsedMs; set => SetProperty(ref _lastElapsedMs, value); }

        /// <summary>上次调用时间戳</summary>
        public string LastCallTime { get => _lastCallTime; set => SetProperty(ref _lastCallTime, value); }

        // 结构化结果字段(用于 KV 显示)
        public string ResultReturnType { get => _resultReturnType; set => SetProperty(ref _resultReturnType, value); }
        public string ResultReturnValue { get => _resultReturnValue; set => SetProperty(ref _resultReturnValue, value); }
        public string ResultInstanceType { get => _resultInstanceType; set => SetProperty(ref _resultInstanceType, value); }
        public string ResultThread { get => _resultThread; set => SetProperty(ref _resultThread, value); }
        public bool ResultSuccessFlag { get => _resultSuccessFlag; set => SetProperty(ref _resultSuccessFlag, value); }
        public string ResultFullMessage { get => _resultFullMessage; set => SetProperty(ref _resultFullMessage, value); }

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

        /// <summary>
        /// 程序集集合变化后刷新统计。
        /// Total*Count 都是计算属性（不从字段读），ObservableCollection.Clear/Add 不会触发它们，
        /// 必须显式通知，否则状态栏与"API 树"N 类型/方法 数字会停留在上一次的值。
        /// </summary>
        private void NotifyAssemblyStats()
        {
            OnPropertyChanged(nameof(TotalTypesCount));
            OnPropertyChanged(nameof(TotalMethodsCount));
            OnPropertyChanged(nameof(TotalPropertiesCount));
            OnPropertyChanged(nameof(TotalFieldsCount));
            OnPropertyChanged(nameof(TotalNativeDllsCount));
            OnPropertyChanged(nameof(LoadedAssemblySummary));
        }

        /// <summary>签名文本(绑定显示)</summary>
        public string SignatureText { get => _signatureText; set => SetProperty(ref _signatureText, value); }

        /// <summary>当前加载的设备路径(工具栏显示用)</summary>
        public string CurrentLoadedPath
        {
            get => _currentLoadedPath;
            set
            {
                if (SetProperty(ref _currentLoadedPath, value))
                    OnPropertyChanged(nameof(LoadedAssemblySummary));
            }
        }

        /// <summary>
        /// 底部状态栏"当前设备"摘要：机型 · 型号 DLL（状态栏里那一格）。
        /// 注意：Assemblies[0] 恒为基类 SDK OptoFidelity.BaseTester（所有机型都一样），
        /// 显示它等于没显示，所以这里取"非 BaseTester 的那个"= 型号 DLL，再拼上机型。
        /// </summary>
        public string LoadedAssemblySummary
        {
            get
            {
                if (Assemblies.Count == 0) return "(未加载)";

                var model = Assemblies.FirstOrDefault(a =>
                                string.IsNullOrEmpty(a.Name)
                                || a.Name.IndexOf("BaseTester", StringComparison.OrdinalIgnoreCase) < 0)
                            ?? Assemblies[0];

                var version = string.IsNullOrEmpty(model.Version) ? "" : $" (v{model.Version})";
                return string.IsNullOrWhiteSpace(_machineTypeText)
                    ? $"{model.Name}{version}"
                    : $"{_machineTypeText} · {model.Name}{version}";
            }
        }

        /// <summary>当前选中成员的依赖项(用于 "依赖项" Tab)</summary>
        public List<DependencyInfo> MethodDependencies
        {
            get => _methodDependencies;
            set => SetProperty(ref _methodDependencies, value);
        }

        /// <summary>高级 JSON 参数(整组按位置覆盖逐参数输入;切换选中项时自动清空)</summary>
        public string JsonArgsOverride { get => _jsonArgsOverride; set => SetProperty(ref _jsonArgsOverride, value); }

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

        /// <summary>P/Invoke 模板文本（绑定到右侧文本框）</summary>
        public string PInvokeTemplate { get => _pInvokeTemplate; set => SetProperty(ref _pInvokeTemplate, value); }

        /// <summary>右栏标签页索引(0 = 调用历史, 1 = 原生 DLL)</summary>
        public int RightTabIndex { get => _rightTabIndex; set => SetProperty(ref _rightTabIndex, value); }

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
        public ICommand CopyPInvokeCommand { get; }
        public ICommand RefreshPInvokeCommand { get; }
        public ICommand RefreshNativeDllsCommand { get; }
        public ICommand SearchFocusCommand { get; }

        // ============== 构造 ==============
        public MainViewModel()
        {
            // 服务初始化 —— 注意顺序：_invoker 依赖 _logBuffer 作为日志出口
            _settingsService = new SettingsService();
            _deviceManager = new DeviceManager();
            _historyService = new HistoryService();
            _themeManager = new ThemeManager(isDark =>
            {
                _isDarkTheme = isDark;
                OnPropertyChanged(nameof(IsDarkTheme));
            });
            _invoker = new ApiInvoker(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher, _logBuffer);

            // 额外的依赖搜索目录（写在 settings.json，避免把私有构建产物路径硬编码进代码）
            foreach (var dir in _settingsService.Settings.ExtraDependencySearchPaths ?? new List<string>())
                _dependencyResolver.AddSearchPath(dir);

            // 安装 AssemblyResolve 处理器：解决 Costura 嵌入版与文件路径版的冲突
            // （BinocRainbow 嵌入的 BaseTester 在 LoadFile 上下文中被反射时找不到外部依赖）
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            // 解析结果一并写进实时日志：命中会写明从哪个目录取到，
            // 未命中会列出全部搜索路径 —— 否则用户只看到 CLR 的 FileNotFoundException，无从下手
            _dependencyResolver.OnDiagnostic = AppendLog;

            // 加载已保存的设置
            _themeMode = string.IsNullOrWhiteSpace(_settingsService.Settings.Theme)
                ? "Light" : _settingsService.Settings.Theme;
            _isDarkTheme = ThemeManager.ResolveIsDark(_themeMode);
            _maxLogLines = _settingsService.Settings.LogRetentionLines > 0
                ? _settingsService.Settings.LogRetentionLines : 5000;

            // 加载已注册设备与历史
            foreach (var d in _deviceManager.Devices) _devices.Add(d);
            foreach (var h in _historyService.Items) _historyItems.Add(h);

            // ApiInvoker 回调（ApiInvoker 内部已切回 UI 线程）
            _invoker.OnStatusChanged = msg => StatusText = msg;
            _invoker.OnCompleted = OnInvocationCompleted;

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
            ToggleThemeCommand = new RelayCommand(() => ThemeMode = _themeManager.IsDark ? "Light" : "Dark");
            OpenSettingsCommand = new RelayCommand(OpenSettings);
            ClearHistoryCommand = new RelayCommand(ClearHistory);
            ClearLogCommand = new RelayCommand(() => LogText = "");
            ExportLogCommand = new RelayCommand(ExportLog);
            SearchClearCommand = new RelayCommand(() => SearchText = "");
            ShowNativeDllsCommand = new RelayCommand(ShowNativeDlls);
            CopyPInvokeCommand = new RelayCommand(CopyPInvoke);
            RefreshPInvokeCommand = new RelayCommand(UpdatePInvokeTemplate);
            RefreshNativeDllsCommand = new RelayCommand(RefreshNativeDlls);
            SearchFocusCommand = new RelayCommand(SearchFocus);

            // 应用主题（窗口还没创建时延后到 Loaded，否则读不到 MainWindow）
            _themeManager.ApplyDeferred(_themeMode);

            // 启动时自动加载默认设备目录
            TryAutoLoadDefaultDevice();
        }
    }
}
