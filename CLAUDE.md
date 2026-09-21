# CLAUDE.md

本文件为 Claude Code 提供项目工作指引。

## 项目概述

**MotionApiTester** 是一个 WPF 桌面工具，用于加载 OptoFidelity 设备 DLL，通过反射枚举 API 并调用方法与构造函数。

**核心场景**：管理多台 OptoFidelity 设备。每台设备 = `OptoFidelity.BaseTester.dll`（共享基类）+ `OptoFidelity.{Model}.dll`（机型实现）+ 可选的 `LTSMC.dll`（雷赛电机原生 C++ SDK）+ `log4net.dll`。

真实 DLL 来源：`D:\Motion\OptoMotionLib\src\Bin_Library\`

## 硬性约束（改代码前必读）

### 1. 强制 .NET Framework 4.8
OptoFidelity 的 DLL 用 Costura.Fody 打包了 76 个依赖，需要 `.cctor` 执行才能展开。.NET 8+ 会跳过 `.cctor`，导致 Costura 不展开嵌入依赖。

### 2. 必须 x64（`PlatformTarget=x64`）
设备 DLL、`LTSMC.dll` 与本工具 exe **全部是 x64**。csproj 里已设 `PlatformTarget=x64` + `Prefer32Bit=false`，**不要改回 AnyCPU/x86**。历史上因为架构不匹配出现过 31 次
`BadImageFormatException: 此程序集是为其他处理器编译的`（见 `%TEMP%\MotionApiTester\load-error.log`）。
注意 `.sln` 里的平台名仍是 `Any CPU`，实际架构由 csproj 的 `PlatformTarget` 决定。

### 3. 旧格式 .csproj
源生成器（`[ObservableProperty]`、`[RelayCommand]`）不工作，**属性手写**；新增文件要手工加进 csproj 的 `<Compile Include>`。

### 4. 不引入第三方 DI
OptoFidelity 内部用 Prism.DryIoc，工具只用构造函数注入。

### 5. 界面颜色一律走主题令牌
`MainWindow.xaml` / code-behind 里**不要硬编码颜色**，用 `{DynamicResource XxxBrush}`。
两套主题 `Themes/LightTheme.xaml` 与 `Themes/DarkTheme.xaml` 的键必须**完全对称**（当前各 30 个键）。
可用语义令牌：`SuccessBg/Fg`、`DangerBg/Fg`、`WarnBg/Fg`、`InfoBg/Fg`、`PurpleBg/Fg`、`NeutralBg/Fg`、`CodeBg/Fg`，以及
`Background/Panel/Text/SubText/Accent/Border/StatusBar/BottomBar/LogBackground/LogForeground/RowSelected`。

## 构建命令

使用 **MSBuild**（旧格式 .csproj）：

```bash
MSBUILD="/c/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe"

# 解决方案在 src/ 下（不是仓库根目录）
"$MSBUILD" "D:/MotionApiTester/src/MotionApiTester.sln" -p:Configuration=Debug '-p:Platform=Any CPU' -m -v:minimal

# 还原 NuGet
"$MSBUILD" "D:/MotionApiTester/src/MotionApiTester.sln" -t:restore -v:minimal
```

### 编译校验的替代通道

`dotnet build` 直接编这个旧格式工程时会**解析不到 PackageReference**（System.Text.Json / CommunityToolkit.Mvvm），
报 CS0234 / CS0246，这不是代码问题。需要编译校验时改用 `.verify/Verify.csproj`（SDK 风格，glob 复用 `src/**/*.cs` + `obj/Debug/*.g.cs`）：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "D:\MotionApiTester\.verify\Verify.csproj" -c Debug --nologo
```

`.verify/` 只用于验证 C# 能否编译通过，**不产出正式程序集**，已在 .gitignore 中。
注意它不校验 XAML，XAML 改动需另外检查（可用 XML 解析器做基本结构校验）。

## 目录结构

```
D:\MotionApiTester\
├─ Bin\                     设备 DLL 存放目录（厂商二进制，未入库）
├─ src\                     工程本体
│  ├─ MotionApiTester.sln
│  ├─ MotionApiTester.csproj
│  ├─ App.xaml(.cs)         全局异常兜底 → %TEMP%\MotionApiTester\crash.log
│  ├─ MainWindow.xaml(.cs)  主界面 + IL 签名 / 结果卡片
│  ├─ Models\               ApiMethod / ApiProperty(ApiField) / ApiAssembly(ApiType) / DeviceProfile / ...
│  ├─ Services\             见下
│  ├─ Themes\               LightTheme.xaml / DarkTheme.xaml
│  ├─ ViewModels\           MainViewModel / TreeNodeVm / Converters
│  └─ Views\                SettingsWindow / DeviceWizardWindow
└─ .verify\                 编译校验专用工程（gitignore）
```

## 架构说明

### MVVM
- `MainViewModel : ObservableObject`，属性手写（见硬性约束 3）
- 自定义 `RelayCommand` / `AsyncRelayCommand`，通过 `CommandManager.RequerySuggested` 自动重查 CanExecute
- 关键：**`SelectedMethod` / `SelectedProperty` / `SelectedField` 的 setter 必须调用 `CommandManager.InvalidateRequerySuggested()`**，
  否则"调用方法"按钮的可用状态不刷新

### 核心服务（`Services/`）
| 服务 | 职责 |
|------|------|
| `DeviceDirectoryScanner` | 扫描目录，按命名规则识别 DLL 角色 |
| `MachineTypeReader` | 读 `D:\MotionConfig\ConfigHardware\MachineType.json` |
| `MachineTypeMatcher` | Jaccard 相似度匹配机型字符串与 `OptoFidelity.{Model}.dll` |
| `AssemblyLoader` | 单程序集加载 + Costura 检测（**注意：MainViewModel 目前走的是自己的 byte[] 加载路径**） |
| `ReflectionEnumerator` | 容错枚举类型 / 方法 / **构造函数** / 属性 / 字段 |
| `ApiInvoker` | 实例解析 + 参数装配 + logger 注入 + 反射调用 |
| `NativeDllInspector` | PE 头解析：架构识别 + P/Invoke 模板生成（导出表枚举尚未实现） |
| `DeviceManager` / `HistoryService` / `SettingsService` | 持久化到 `%APPDATA%\MotionApiTester\` |
| `LogService` | 当前未被使用（日志走 ApiInvoker 队列 + `MainViewModel.FlushLogQueue`） |

### 调用链路
1. `MainViewModel.LoadDeviceFromDirectory()`
   → 扫描目录 → 匹配机型 DLL → `File.ReadAllBytes` → `Assembly.Load(byte[])`（`_assemblyByteCache` 供 `AssemblyResolve` 复用）
   → `ReflectionEnumerator.Enumerate()`
   → `_invoker.SetCandidateAssemblies(_loadedAssemblies)`
2. `InvokeSelectedAsync()` → `ApiInvoker.InvokeAsync(method, JsonArgsOverride)`
3. `ApiInvoker` 内部顺序：
   - `BuildParameters()`：logger 参数（`Action<string>`）注入回调 → 用户勾选的 `PassNull` → `DefaultValue` → `ConvertValue` 逐参数转换；
     若填了 JSON 数组则**按位置整组覆盖**（个数必须与参数个数一致，否则回退）
   - 构造函数：`ConstructorInfo.Invoke(args)` 直接返回新实例
   - 实例方法：`ResolveInstance(DeclaringType)` —— 具体类型直接 `Activator.CreateInstance`；
     **接口 / 抽象类**则在候选程序集里找"可无参构造的具体实现"（优先机型 DLL、其次类名最短），结果缓存
   - 实例按类型缓存复用（**同一类型重复调用共享实例，保留设备内部状态**）
   - `Target.Invoke(instance, args)`，返回值与异常全部写入 `CallHistoryItem` + 日志队列

### 关于 `ApiMethod` 的两个反射入口
`ApiMethod.MethodInfo` 只对普通方法非空，构造函数放在 `ApiMethod.ConstructorInfo`，
统一入口是 `ApiMethod.Target`（`MethodBase`）。**新增代码一律用 `Target`**，
只有在需要 `ReturnType` / `IsGenericMethod` 等 MethodInfo 专有成员时才显式判型 `MethodInfo`。

## 界面结构

```
Row0 标题栏（品牌 + MachineType）   Row1 工具栏（加载/卸载/路径 + 设备下拉 + 搜索 + 主题 + 设置）
Row2 状态条（StatusText）
主内容（Row2 RowSpan=3，覆盖式叠层）：
   左 280px  API 树（Assembly → Namespace → Type → 分组 → 成员）
   中  *      方法详情（修饰符标签 / 签名 / 参数输入 / 原始签名(IL) / XML 文档 / 依赖项）
   右 320px  标签页：调用历史 | 原生 DLL（列表 + P/Invoke 模板）
Row4 Expander 实时日志          Row5 底部状态栏（计数 + 快捷键）
```

注意事项：
- **不要再给 `TreeView` 设置 `ItemContainerStyle`**：它会覆盖 `Window.Resources` 里的隐式 `TreeViewItem` 样式，
  导致选中行的蓝色左边框（`RowSelectedBrush`）失效
- 结果卡片头部颜色由 `MainWindow.xaml.cs: UpdateResultCardStyle()` 用 `SetResourceReference` 动态切换成功/失败令牌

## 快捷键

| 键 | 动作 |
|----|------|
| Ctrl+O | 重新加载当前设备目录 |
| F5 | 调用选中项 |
| Ctrl+F | 聚焦搜索框 |
| Esc | 清空搜索 |
| Ctrl+L | 清空日志 |
| Ctrl+H | 清空历史 |

## 用户目录

| 路径 | 用途 |
|------|------|
| `D:\MotionApiTester\Bin` | 用户放置设备 DLL，工具默认从这里加载（可被 `settings.json` 的 `DefaultDeviceDirectory` 覆盖） |
| `D:\MotionConfig\ConfigHardware\MachineType.json` | 机型字符串（只读） |
| `%APPDATA%\MotionApiTester\settings.json` | 主题 / 日志行数 / 超时 / 默认目录 |
| `%APPDATA%\MotionApiTester\devices.json` | 已登记设备（`DeviceManager`） |
| `%APPDATA%\MotionApiTester\devices\default\history.json` | 调用历史（deviceId 目前硬编码 `default`） |
| `%TEMP%\MotionApiTester\crash.log` | 未处理异常 |

`.gitignore` 忽略了 `Bin\` 与 `src\bin` / `src\obj`，因此**克隆后需要手动把设备 DLL 拷进 `Bin\`**。

## 当前状态

**已实现**
- DLL 扫描 / 角色识别 / 机型模糊匹配 / byte[] 加载 + AssemblyResolve 兜底
- 反射枚举：类型 / 方法 / 构造函数 / 属性 / 字段，过滤编译器生成成员
- API 三栏树 + 搜索过滤 + 右键复制 + 展开折叠
- 方法 / 构造函数 / 属性 / 字段调用（含接口实现自动解析、logger 注入、JSON 整组参数、传 null）
- 实时日志面板（队列 + 100ms 刷新）+ 调用历史（持久化）
- 原生 DLL 面板（PE 架构识别 + P/Invoke 模板）
- 多设备登记与切换（`devices.json`）
- 浅色 / 深色 / 跟随系统主题（语义令牌，界面零硬编码颜色）
- 设置窗口、全局异常兜底

**未实现 / 已知限制**
- `NativeDllInspector` 只生成占位模板，**未真正解析导出表**（`GeneratePInvokeTemplate` 里的函数名是 TODO）
- "取消调用"只能取消尚未开始的任务；`MethodBase.Invoke` 是同步阻塞调用，无法中断已在设备侧执行的指令
- "卸载"只清界面与缓存：`Assembly.Load` 无法从 AppDomain 卸载
- 接口实现必须**可无参构造**，否则仍会失败（会给出明确错误信息）
- 调用历史未按设备隔离（deviceId 硬编码 `default`）
- 搜索每次按键全量重建整棵树，大程序集（5MB Costura）下会卡顿
