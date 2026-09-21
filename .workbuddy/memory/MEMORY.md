# MotionApiTester 项目长期记忆

## 工程约定（改代码必须遵守）

- **必须 .NET Framework 4.8 + `PlatformTarget=x64`**。设备 DLL、`LTSMC.dll`、本工具 exe 全是 x64；
  改成 AnyCPU/x86 会立刻出现 `BadImageFormatException: 此程序集是为其他处理器编译的`
  （已写入 CLAUDE.md 的硬性约束章节）。
- **旧格式 .csproj**：源生成器不工作（属性手写）；新增 .cs 文件必须手工加进 `<Compile Include>`。
  但 `.verify/Verify.csproj` 用 glob 复用 `src/**/*.cs`，所以新增文件不影响编译校验。
- **界面颜色一律走主题令牌**：不要硬编码颜色。两套主题键必须完全对称
  （`Themes/LightTheme.xaml` / `DarkTheme.xaml`，当前各 54 个键，必须对称）。
- **`ApiMethod` 有两个反射入口**：方法用 `MethodInfo`，构造函数用 `ConstructorInfo`，
  统一走 `ApiMethod.Target`（`MethodBase`）。新增代码用 `Target`。
- **`SelectedMethod` / `SelectedProperty` / `SelectedField` 的 setter 必须调用
  `CommandManager.InvalidateRequerySuggested()`**，否则"调用方法"按钮可用状态不刷新。
- **不要给 `TreeView` 设置 `ItemContainerStyle`**：会覆盖 `Window.Resources` 里的隐式
  `TreeViewItem` 样式，导致选中行高亮失效。
- 内置 `BooleanToVisibilityConverter` **忽略 ConverterParameter**，需要反向可见性时
  必须用自定义的 `InverseBoolToVisibilityConverter`。
- **窗口不使用系统标题栏**：`WindowStyle="None"`，窗口外壳行为交给 `WindowChrome`
  （`CaptionHeight="32"` → 顶部拖动区 + 双击最大化/还原 + 右键系统菜单；
  `ResizeBorderThickness="6"` → 边缘缩放；已有 `GlassFrameThickness=0`、`UseAeroCaptionButtons=False`）。
  自绘标题栏在 `MainWindow.xaml` 顶层 `Grid` 的 `Row 0`，窗口按钮（─ □ ✕）在其右端。
  **标题栏区域内的按钮必须设 `shell:WindowChrome.IsHitTestVisibleInChrome="True"`**，
  否则点击会被 WindowChrome 当成拖动窗口。
- `MainWindow.xaml` 顶层 `Grid` 共 **6 行**：`0` 标题栏 / `1` 工具栏 / `2` 状态栏 /
  `3` 主内容三栏（弹性行）/ `4` 实时日志 Expander / `5` 底部状态栏。机型文本（`MachineTypeText`）挂在标题栏右端。
  **各行独立占位，不要再回到负 Margin + RowSpan 的覆盖式叠压**：原先主内容那样压在状态栏与日志之上，
  日志展开后被整块遮住（用户报「实时日志显示不全」），现已改掉。
- **最大化不裁切**：`WindowStyle=None` + `WindowChrome` 组合下，WindowChrome 会把最大化尺寸算成
  「工作区 + 边框补偿」，而客户区等于整个窗口 → 内容溢出屏幕、右侧与底部各被裁一个边框宽度。
  现由 `MainWindow.OnSourceInitialized` 挂 `WM_GETMINMAXINFO` hook 接管：用 `MonitorFromWindow` +
  `GetMonitorInfo` 的 `rcWork` 钉死最大化尺寸，并自行把 `ptMinTrackSize` 设为 `MinWidth/MinHeight`
  （接管后 WindowChrome 不再负责这部分）；`StateChanged` 里把 `ResizeBorderThickness` 归零做双保险。
  ⚠️ `ResizeBorderThickness` / `CaptionHeight` 是 WindowChrome 的**实例属性，没有静态 setter**
  （与 `IsHitTestVisibleInChrome` 这个附加属性不同）。取实例用 `WindowChrome.GetWindowChrome(window)`。
- **应用图标**：`src/app.ico`（16~256 共 7 个尺寸）。csproj 的 `<ApplicationIcon>` 只决定 exe 文件图标，
  **必须另行登记 `<Resource Include="app.ico" />`**，`Window.Icon="app.ico"` 与标题栏
  `<Image Source="app.ico"/>` 才能引用。漏登记会在启动时抛 `XamlParseException`。
  验证办法：用 Python 搜 `src/obj/Debug/MotionApiTester.g.resources` 里有没有 `app.ico` 这个条目。
- **实时日志面板**（`Row 4`）显示不全有两个成因，都已修：① 默认横向滚动条是 `Hidden`，
  长日志行被直接截断 → 需 `HorizontalScrollBarVisibility="Auto"`；② `Text` 追加在末尾但 TextBox
  不跟随滚动 → 在 `TextChanged` 里 `ScrollToEnd()`（仅当视口原本贴底，避免打断向上翻阅）。

## 架构约定（第三轮重构后）

- **`MainViewModel` 是 `partial`，按职责分 7 个文件**：核心 / `.Device` / `.Invocation` / `.Tree` /
  `.NativeDll` / `.Theme` / `.Log`。新增成员按职责放对应文件，不要往核心文件堆。
- **代码里不写死绝对路径**：设备目录走 `DeviceDirectoryResolver`，依赖解析路径走 `DependencyResolver`，
  私有构建产物目录通过 `settings.json` 的 `ExtraDependencySearchPaths` 配。
  （历史上 `MainViewModel._extraSearchPaths` 与 `AppSettings.DefaultDeviceDirectory` 各有硬编码，
  第二轮自查因 shell 转义失效漏报过——**扫路径用 Grep 工具或 Python，别用 bash 转义**。）
- **日志出口是 `LogTextBuffer`**：`ApiInvoker` 只往 sink 写，队列与保留行截断都在 buffer 里，
  UI 侧 100ms 定时器 Flush 成 `LogText`。
- **树构建是 `ViewModels/TreeBuilder.cs`**（放 ViewModels 而非 Services，因为它产出 `TreeNodeVm`，
  避免 Services→ViewModels 的反向依赖）。
- **改 XAML 绑定或增删源文件后跑 `python tools/verify-structure.py`**：编译管不到绑定失效与
  csproj 漏登记（`AssemblyInfo.cs` 就漏了很久，导致程序集版本号与 `ThemeInfo` 一直缺失）。

## 编译校验通道（重要）

本环境的 Bash/PowerShell 工具把 `MSBuild.exe` 判为 LOLBin 并拦截，**无法直接调用 MSBuild**。
`dotnet build src/MotionApiTester.csproj` 也不行：旧格式工程解析不到 PackageReference
（System.Text.Json / CommunityToolkit.Mvvm），会报 CS0234 / CS0246，这不是代码错误。

可用通道：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "D:\MotionApiTester\.verify\Verify.csproj" -c Debug --nologo
```

- `.verify/` 是 SDK 风格的校验工程，glob 复用 `src/**/*.cs` + `src/obj/Debug/*.g.cs`，
  0 error / 0 warning 即代表 C# 侧可用（**不校验 XAML**）。
- XAML 改动后用 Python 的 `ElementTree` 做结构校验，并核对
  `{DynamicResource}` / `{StaticResource}` 引用的键是否都真实存在
  （缺键会在运行时抛 `XamlParseException`，历史上出现过一次）。
- 输出中文日志乱码时，用 `Out-File -Encoding utf8` 写文件再读取。
- ⚠️ **两个已踩过的坑**：
  1. **与 VS 构建竞态**：本工程 glob `src/obj/Debug/*.g.cs`。用户正在 VS 里构建时，g.cs 恰被重写
     → glob 读空 → 会报几十个「当前上下文中不存在名称 InitializeComponent」的**假错误**。
     重跑一次即恢复 0 error，**不要据此改代码**。
  2. **XAML 新增 `x:Name` 会让校验通道失效**：code-behind 引用新 `x:Name` 需要 g.cs 里有对应字段，
     而 g.cs 只能由 VS 构建生成（本环境生不了 XAML）→ 在 VS 构建之前 `.verify` 必然编译失败。
     **优先用 `sender` / `FindName` 拿控件，不要为了方便就加 `x:Name`**；
     确需新增时，先让用户在 VS 构建一次再跑校验。

## 运行期排障位置

- `%TEMP%\MotionApiTester\load-error.log` — 加载/架构失败
- `%TEMP%\MotionApiTester\diagnostic.log` — 早期加载流程诊断
- `%TEMP%\MotionApiTester\crash.log` — 未处理异常（含 XamlParseException）
- `%APPDATA%\MotionApiTester\devices\default\history.json` — 真实调用结果（判断 API 是否真能调通）
- `src\bin\Debug\Logs\<yyyyMMdd>\log_ui.txt` — 设备侧 log4net 输出
