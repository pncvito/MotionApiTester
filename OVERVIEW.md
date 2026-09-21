# MotionApiTester 项目概述

> 面向**开发者**的文档：这个项目是怎么写的、为什么这么写。
> 怎么用工具见 [`README.md`](README.md)；改代码的硬性约束与 AI 协作约定见 [`CLAUDE.md`](CLAUDE.md)。
> 本文讲的是**结构、机制与取舍**，不是操作手册。

---

## 1. 它解决什么问题

设备侧只有编译好的 .NET 程序集（`OptoFidelity.BaseTester.dll` 共享基类 + `OptoFidelity.{Model}.dll` 机型实现），没有源码，而且不少方法讲究调用顺序（先初始化、再动作）。本工具做三件事：

1. 把设备程序集**加载进自己的进程**，用反射把 API 摊成一棵树；
2. 让使用者能填参数、**真的把方法调起来**，并把结果（返回值 / `out`-`ref` 回填 / 异常 / 设备侧日志）全摊开；
3. 顺带把原生 DLL（如 `LTSMC.dll`）的**导出表**读出来，生成 P/Invoke 模板。

所以它同时是：反射工具 + 进程内加载器 + 诊断面板。真正难的地方不在 UI，而在「**怎么安全地把一堆带内嵌依赖、互相引用、无法卸载的程序集装进一个进程并调用**」。

---

## 2. 技术选型与硬约束（每一条都是被踩出来的）

| 项 | 选择 | 为什么 |
|---|---|---|
| 运行时 | .NET Framework 4.8（`csproj:13`） | 设备 DLL 用 Costura.Fody 内嵌 70+ 依赖，靠 `<Module>::.cctor` 执行时才展开；.NET 8+ 不执行 `.cctor`，内嵌依赖永远展不开 |
| 架构 | x64（`csproj:7-8`） | 设备 DLL / `LTSMC.dll` / 本 exe 全是 x64；混架构直接 `BadImageFormatException`。注意 `.sln` 平台名仍写 `Any CPU`，真实架构由 `PlatformTarget` 决定 |
| 语言版本 | C# 8.0（`csproj:17`） | 旧格式工程 + 4.8 的编译器上限 |
| 工程格式 | **旧格式 .csproj** | 代价：源生成器不工作、新增文件必须手工登记（漏登记**不报错**，只是静默不参与编译） |
| MVVM | CommunityToolkit.Mvvm 8.2.2，只用来拿 `ObservableObject` | 旧格式下 `[ObservableProperty]` / `[RelayCommand]` 不生效，属性与命令一律**手写** |
| JSON | System.Text.Json 8.0.5 | 设置 / 历史 / 参数记忆的读写，以及界面的 JSON 参数入口 |
| DI | 不用容器，构造函数手工装配 | 工具本身不需要容器（设备侧用 Prism.DryIoc 与我们无关） |
| UI | WPF + 自绘标题栏（`WindowStyle=None` + `WindowChrome`） | 要自己的标题栏配色，同时保住边缘缩放与双击最大化 |

---

## 3. 分层与依赖方向

```
Views / XAML              MainWindow(.xaml/.cs)、Views/SettingsWindow、Views/DeviceWizardWindow、Themes/*
      ↓ 数据绑定 / 事件
ViewModels                MainViewModel（7 个 partial）、TreeBuilder、TreeNodeVm、RelayCommand、Converters
      ↓ 调用
Services                  反射枚举、调用管线、依赖解析、原生 DLL 解析、目录/机型、持久化、主题、日志缓冲
      ↓
Models                    纯数据：ApiAssembly/ApiType、ApiMethod、ApiProperty、ApiField、DeviceProfile、CallHistoryItem、NativeDllInfo…
```

几条贯穿全局的约定：

- **依赖方向单向**：Services 不认识 ViewModel。需要回传时用回调属性：`ApiInvoker.OnCompleted / OnStatusChanged`、`DependencyResolver.OnDiagnostic`、`ThemeManager` 构造里传 `Action<bool>`。
- **所有耗时与反射操作都在服务里**，ViewModel 只做编排与状态同步。
- **颜色不进 C#**：树节点模板按 `NodeKind` / `Badge` 用 DataTrigger 映射主题令牌，`TreeBuilder` 只给数据（`TreeNodeVm.cs` 里没有颜色）。

---

## 4. 代码规模（实测，不含 `obj/` 生成文件）

| 目录 | 文件 | 行数 | 说明 |
|---|---|---|---|
| `src/Services/` | 18 | 3274 | 最重的一层：`ApiInvoker` 852、`DependencyResolver` 558、`NativeDllInspector` 312、`ReflectionEnumerator` 269 |
| `src/ViewModels/` | 11 | 1969 | `MainViewModel.Device` 498、`MainViewModel` 394、`MainViewModel.Invocation` 353、`TreeBuilder` 222 |
| `src/Models/` | 8 | 293 | 纯数据 + 少量展示辅助 |
| `src/Views/` | 2 | 239 | 设置窗口、设备向导的 code-behind |
| `src/` 根 | 2 | 460 | `MainWindow.xaml.cs` 424（快捷键、窗口外壳、结果卡片）、`App.xaml.cs` 36 |
| XAML | 6 | 1337 | `MainWindow.xaml` 900、`SettingsWindow` 154、`DeviceWizardWindow` 91、两套主题各 73、`App` 46 |
| **合计** | **41** | **6252** | .cs 合计含 `Properties/AssemblyInfo.cs`（17 行），不含 `obj/` 生成文件 |

---

## 5. 核心机制

### 5.1 反射枚举与 API 树

`ReflectionEnumerator` 用**容错枚举**：`Assembly.GetTypes()` 抛 `ReflectionTypeLoadException` 时退而从 `ex.Types` 取非空项，其余异常写 `%TEMP%\MotionApiTester\enum-errors.log`（只记前 5 条 LoaderException），保证"一半类型坏掉"也不至于全盘失败。

几个刻意的取舍：

- 全部用 `DeclaredOnly`（方法/构造函数/属性/字段）。**这直接决定了后面实例解析必须跨继承链复用对象** —— 成员只挂在声明它的类型节点下，`BaseInterface.InitializeFixture` 与 `EolSeriesBaseInterface.FixtureMoveTo...` 是两个节点。
- 过滤 `IsSpecialName`（属性访问器）、`IsCompilerGenerated`、非 public 类型、名字带 `<` 的成员。
- 构造函数必须单独用 `GetConstructors()`：`GetMethods()` 不返回构造函数。
- 颜色不让服务决定：`TreeBuilder` 只产出 `NodeKind` / `Badge`，视觉映射在 XAML 的节点模板里。

### 5.2 调用管线（`ApiInvoker`）

参数装配的**固定优先级**（`BuildParameters`）：JSON 整组覆盖 → logger 参数注入 → 用户勾选的 `PassNull` → 可选参数默认值 → 类型默认值 → 逐参数 `ConvertValue` 转换。几条硬经验：

- **结果判定要看两处**：先看是否抛异常，再看返回值是否形如 `(false, "…")`（设备 API 普遍把失败 `catch` 成返回值，不向调用方抛）。只看异常会把失败报成「✅ 调用成功」，比直接报错更误导。
- **`out` / `ref` 的值类型不能传 null**（`Invoke` 会抛 `ArgumentException`），必须按**元素类型**取默认值；调用后再把回填值拼出来显示。设备 API 大量用 out 传结果。
- 参数日志打的是**转换后的实际值**，不是用户输入的原文 —— 否则转换失败时日志会把排查方向带偏。
- JSON 只支持到「数组」这一层，遇到对象类型**明确报错并按 null 传入**（以前是静默变 null，最难查）。
- **实例解析**：① 先在已建实例里找「能赋给声明类型」的那个（`FindReusable`，取继承链上派生最深的）；② 否则在候选程序集里找**派生最深**的可无参构造实现（`FindImplementation` + `_implementationCache`）。显式调用过构造函数后，新实例会登记为该类型的当前实例。
- **超时与取消的边界是诚实的**：`MethodBase.Invoke` 是同步阻塞调用，超时只是「界面不再干等」，取消只对尚未开始的任务有效 —— **无法中断设备侧已经在跑的指令**。这条在代码注释、README、CLAUDE.md 里都写了，因为「超时后重发动作指令」是会造成机械事故的误用。

### 5.3 程序集加载与依赖解析（项目里最难的一块）

设备 DLL 是 **Costura 打包的**：依赖都嵌在资源里，磁盘上没有文件。于是有两个后果：磁盘上"缺文件"是正常的；而嵌入的解析器要**主动激活**才会注册（`Assembly.Load(byte[])` + 纯元数据反射从不执行设备侧代码，`.cctor` 永不触发）。

加载顺序（`MainViewModel.LoadDeviceFromDirectory`，顺序不可颠倒）：

1. `File.ReadAllBytes` → `Assembly.Load(modelBytes)`（**机型 DLL 优先**，它是 Costura 宿主）；
2. `CosturaActivator.TryActivate(modelAsm)` 激活内嵌解析器 → 成功则 `RegisterEmbeddedProvider`；
3. `DependencyResolver.Register(简名, 实例)` 登记**程序集实例**；
4. 再加载并登记 `BaseTester`；
5. `ReflectionEnumerator.Enumerate(...)` → `ApiInvoker.SetCandidateAssemblies(...)`。

`DependencyResolver` 承担 `AssemblyResolve`：

- **搜索路径优先级**：设备目录（`SetDeviceDirectory` 插到最前，并**摘掉上一台设备的目录**）→ 本程序输出目录 → `%TEMP%\MotionApiTester` → NuGet 全局包目录。
- **缓存的是 `Assembly` 实例，不是字节**。`Assembly.Load(byte[])` 在 .NET Framework 下不去重，同一份字节加载两次得到两个程序集，而程序集永不卸载 —— 每次重复加载 = 一份永久泄漏的副本（机型 DLL 约 5MB）。所以「只为取个名字」也要用 `AssemblyName.GetAssemblyName(path).Name`（只读元数据、不加载）。
- **未命中不能立刻喊"缺失"**：`AssemblyResolve` 是多播事件，本处理器返回 null 只代表"我这条路没找到"，Costura 的处理器还在后面。所以未命中会挂起，**延迟 400ms**（`MissFlushDelayMs`）后按「最终是否真的加载进进程」裁决。
- **依赖体检分四桶**（`DependencyAudit`）：进程内已加载 / 来自内嵌资源 / 文件系统能找到 / 按需 `Assembly.Load(简名)` 成功，剩的才算缺失；最后落 `%TEMP%\MotionApiTester\audit.log`。判「框架程序集」必须查 CLR 运行时目录，不能用 `System.` 前缀（会错杀 `SystemManager.dll`）。
- 另有一套**独立**的原生 DLL 搜索机制：`NativeSearchPath` 调 `SetDllDirectory` 指向设备目录。原生 P/Invoke 与托管程序集解析互不兜底（`0x8007007E` 常被误读成"缺依赖"，其实是缺**路径**）。

### 5.4 原生 DLL：手工 PE 解析

`NativeDllInspector` **只读文件字节，绝不 `LoadLibrary`** —— 加载原生 DLL 会执行它的 `DllMain`，等于拿本进程冒险。手工按 PE 规范走：MZ/PE 签名 → COFF 头 → 可选头判 32/64 位 → 数据目录第 0 项（导出表）→ 节表 `RVA → 文件偏移` → 导出目录的 6 个字段 → 遍历名字表。

细节：x86 stdcall 的 `_name@12` 还原成 `name` 并标注调用约定，但 `EntryPoint` 用**原始名**（写 `name` 会找不到入口点）；函数地址落在导出目录区间内即**转发导出**，读该处字符串；`?` 开头的是 C++ 修饰名，原样保留并加告警。生成的模板只是骨架 —— **导出表没有签名信息**，必须照厂商 `.h` 核对。

### 5.5 持久化与键设计

全部落在 `%APPDATA%\MotionApiTester\`：

| 文件 | 负责类 | 关键设计 |
|---|---|---|
| `settings.json` | `SettingsService` | `DefaultDeviceDirectory` 留空 = 自动探测；`ExtraDependencySearchPaths` 用来避免把私有构建产物路径硬编码进代码 |
| `devices.json` | `DeviceManager` | `CheckDeviceReady` 会区分「缺 BaseTester / 缺机型 DLL」（阻塞）与「缺 `LTSMC.dll`」（警告：设备可能无法动作） |
| `devices\<机型 DLL 名>\history.json` | `HistoryService` | **按设备隔离**；首次使用某设备时自动从旧的 `default/` 迁移一次；最多 1000 条，新的插队首 |
| `param-values.json` | `ParameterMemory` | 键 = **声明类型全名 + `MethodBase.ToString()`**。`ToString()` 不含声明类型，只用它会让不同类的同名同参方法（构造函数最典型）串味；只记**成功调用**的参数值 |

### 5.6 线程模型与日志

- 设备调用跑在 `Task.Run` 里，日志从任意线程入 `LogTextBuffer`（`ConcurrentQueue` + 时间戳），UI 侧由 **100ms `DispatcherTimer`** 批量取走并按 `LogRetentionLines` 截断 —— 避免在后台线程改 `INotifyPropertyChanged`。
- 回调切线程集中在 `ApiInvoker.Post()`：`Dispatcher.CheckAccess()` 判断，否则 `BeginInvoke`。
- 全局异常兜底在 `App.xaml.cs`：`DispatcherUnhandledException` 置 `Handled = true` 不让程序挂掉，并写 `%TEMP%\MotionApiTester\crash.log`。

### 5.7 UI 层的关键做法

| 做法 | 原因 |
|---|---|
| 快捷键写在 `MainWindow.OnPreviewKeyDown`（`MainWindow.xaml.cs:159-204`），不用 XAML `KeyBinding` | `KeyBinding` 是 Freezable、不在可视树上，声明在 `<Window.DataContext>` 之前时绑定要等首次 Show 才解析；`CanExecute=false` 时**按下去毫无反应且不报错**。Preview 阶段还能对"没选中方法"给出显式状态栏提示 |
| 自绘标题栏 + 接管 `WM_GETMINMAXINFO`（`MainWindow.xaml.cs:36-86`） | `WindowStyle=None` 后，WindowChrome 最大化会把尺寸算成「工作区 + 边框补偿」导致内容溢出屏幕；这里在工作区尺寸上钉死，并自行承担最小尺寸（含 DPI 换算） |
| 标题栏按钮必须 `WindowChrome.IsHitTestVisibleInChrome=True` | 不开的话点击会被当成拖拽窗口 |
| 树展开/折叠走**数据**（`TreeNodeVm.IsExpanded` 双向绑定） | 树开了虚拟化，`ItemContainerGenerator` 拿不到未实化的容器，旧写法只能展开一两层 |
| **不要**给 `TreeView` 设 `ItemContainerStyle` | 会盖掉 `MainWindow.xaml:120-133` 的隐式 `TreeViewItem` 样式，选中行的蓝色左边框失效 |
| 结果卡片头色在 code-behind 用 `SetResourceReference` 切换（`MainWindow.xaml.cs:292-305`） | 成功/失败要换整套令牌，用绑定写会重复一堆转换器 |
| 不用 `ComboBox` | 它的默认模板**无视 `Background`**，要跟主题必须换整个 `ControlTemplate`。于是导出格式改单选按钮、向导的机型 DLL 改 ListBox |
| 输入类控件在 `App.xaml` 给隐含样式（`:21-44`） | WPF 默认模板用**系统色**，深色主题下会冒出白色输入框 |
| 日志自动滚动只在「视口贴底」时进行（`TbLog_TextChanged`） | 否则用户向上翻阅历史时会被强行拉回 |

### 5.8 主题系统

`ThemeManager` 把主题字典挂在 **`Application.Resources`**（不是 `MainWindow.Resources`）—— 设置窗口与设备向导是独立 `Window`，窗口级字典照不到它们，历史上它们因此只能硬编码颜色、永远不跟主题。`Light` / `Dark` / 跟随系统（读注册表 `AppsUseLightTheme`）三态；两套主题的令牌键**必须完全对称**（当前各 54 键）。

---

## 6. 两条主流程

### 6.1 加载设备（启动自动加载 / `Ctrl+O` / 切设备）

```
LoadDeviceCommand → LoadDeviceFromDirectory(deviceDir)
  ├─ 清状态（Assemblies / NativeDlls / 选中节点）
  ├─ DependencyResolver.Clear() + SetDeviceDirectory(deviceDir)      ← 必须摘掉上一台设备的目录
  ├─ MachineTypeReader.Read()（D:\MotionConfig\ConfigHardware\MachineType.json）
  ├─ DeviceDirectoryScanner.Scan()  → 按命名规则给每个 DLL 定角色
  ├─ MachineTypeMatcher.FindBestMatch()（Jaccard 相似度；0 分返回 null，唯一候选时兜底）
  ├─ NativeSearchPath.SetDeviceDirectory()                            ← 原生 DLL 走这一套
  ├─ AssemblyName.GetAssemblyName() 取简名（不加载）→ File.ReadAllBytes → Assembly.Load
  ├─ CosturaActivator.TryActivate + RegisterEmbeddedProvider + Register(实例)
  ├─ ReflectionEnumerator.Enumerate() → Assemblies
  ├─ HistoryService.SwitchDevice()  → 历史跟着设备走
  ├─ DependencyResolver.AuditDependencies() → 日志 + audit.log
  └─ ScanNativeDlls() / RefreshSearch() → 左侧树出现
```

### 6.2 调用方法（`F5`）

```
OnPreviewKeyDown → InvokeCommand.CanExecute? → InvokeSelectedAsync()
  → ApiInvoker.InvokeAsync(method, jsonArgsOverride)
      ├─ BuildParameters()：JSON 覆盖 / logger 注入 / PassNull / 默认值 / ConvertValue
      ├─ 构造函数 → ConstructorInfo.Invoke + 登记实例
      ├─ 实例方法 → ResolveInstance(DeclaringType)（复用同一对象 → 派生最深实现）
      ├─ Target.Invoke(instance, args)（可选超时：Task.WhenAny，仅"不再等待"）
      ├─ 结果判定：异常 + 返回值自报失败(TryReadApiFailure)
      └─ Post(OnCompleted / OnStatusChanged)   ← 切回 UI 线程
  → OnInvocationCompleted：记录历史 → 记住参数值 → 填结果 KV → ResultText
  → MainWindow 订阅 PropertyChanged → UpdateResultCardStyle() 切成功/失败配色
```

---

## 7. 编码约定（改动前必读）

1. **新增 / 删除源文件必须同步改 `csproj`**（`<Compile>` / `<Page>` 手工登记）。漏掉不会报错，只会静默不参与编译。
2. **新增 / 重命名 public 成员后核对 `MainWindow.xaml` 的 `{Binding X}`**：绑定失效是静默的（只表现为界面空白）。
3. **颜色一律走主题令牌**，两套主题键保持对称；次要文字用 `SubTextBrush`，别写 `Foreground="Gray"` / `#E0E0E0`。
4. **`MainViewModel` 按职责放进对应的 partial 文件**（核心文件只放服务/字段/属性/命令 + 构造装配）。
5. `SelectedMethod` / `SelectedProperty` / `SelectedField` 的 setter 必须调 `CommandManager.InvalidateRequerySuggested()`，否则按钮可用状态不刷新。
6. 计算属性（`TotalTypesCount` 等）不会因集合变化自动通知，必须显式 `OnPropertyChanged`（见 `NotifyAssemblyStats`）。
7. 反射入口统一用 `ApiMethod.Target`（`MethodBase`），只有需要 `ReturnType` 这类 `MethodInfo` 专有成员时才判型。
8. 注释写**为什么**，不写**是什么** —— 本仓库的注释密度是有意为之：每条"不能这么写"的旁边都留着当年踩坑的现场。

---

## 8. 构建与验证

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "D:\MotionApiTester\src\MotionApiTester.sln" -t:restore -v:minimal
& $msbuild "D:\MotionApiTester\src\MotionApiTester.sln" -p:Configuration=Debug "-p:Platform=Any CPU" -m -v:minimal
```

- Debug 的输出目录由 csproj 的 `OutputPath` 指定，当前是 **`src\Bin\`**（`csproj:25`，不是默认的 `bin\Debug\`）—— exe、NuGet 依赖与设备库子目录 `src\Bin\MotionAPI\` 都在这一层；Release 仍是 `src\bin\Release\`（`csproj:33`）。
- **不要用 `dotnet build`**：旧格式工程解析不到 `PackageReference`，会报 CS0234 / CS0246 —— 那不是代码问题。
- 需要一个不产出正式产物的编译校验通道时，用 `tools/Verify.csproj`（SDK 风格，glob 复用 `..\src\**\*.cs` 与 `src\obj\**\*.g.cs`）：`dotnet build tools/Verify.csproj -c Debug`。它**不校验 XAML**，XAML 改动要靠下面的脚本另外检查。
- XAML 绑定根、csproj 与磁盘文件是否互相覆盖这类"编译器管不到"的问题，由 `tools/verify-structure.py` 兜（已恢复并跑通：121 处绑定、17 个事件处理器、csproj 清单全部 0 问题）；换图标后校验 `app.ico` 目录表用 `tools/verify-ico.py`（WPF 的 WIC 对坏 ICO 会直接抛 `XamlParseException`，表现为启动即崩）。
- 图标有**两个登记点**，缺一不可：`<ApplicationIcon>`（exe 文件图标）+ `<Resource Include="app.ico" />`（WPF 资源，供 `Icon="app.ico"` 与标题栏 `Image` 引用，见 `csproj:139-142`）。

---

## 9. 已知限制与技术债

**功能上的限制（有意接受）**

- 导出表只有名字与序号，没有签名；P/Invoke 模板必须照厂商 `.h` 核对。
- 超时 / 取消**无法中断**设备侧正在执行的指令；「卸载」只清界面与缓存，`Assembly.Load` 加载的程序集无法从 AppDomain 卸载（要彻底复位只能重启进程）。
- JSON 参数不支持对象类型（数组已支持）。
- 接口实现必须**可无参构造**，否则给明确错误。
- 参数记忆只记成功调用；接口节点与类节点各记各的（`IBaseInterface.X` 与 `BaseInterface.X` 是两套）。
- 依赖缺失报告延迟约 400ms 裁决（多播事件的代价）。
- 搜索是**每次按键全量重建整棵树**，加载 5MB Costura 程序集时会有卡顿。

**技术债**

- `Services/AssemblyLoader.cs` 与 `Services/LogService.cs` **当前没有任何引用点**（实测全仓库只有自身的类声明；实际的加载与日志分别走 `MainViewModel.LoadDeviceFromDirectory` 与 `LogTextBuffer`）。留着的价值是"加载顺序"那份注释，考虑合并或删除。
- `MainWindow.xaml` 里底部状态栏的状态点与「就绪」文字、以及「上次耗时」的颜色是**写死的**（`:855-857`、`:875`），调用失败时仍显示绿色 —— 属"状态在说谎"，应绑定 `IsInvoking` / 上次结果。
- 深色主题只覆盖了**自绘的部分**：默认模板的 `Button`（hover `#BEE6FD` / pressed `#C4E5F6` / disabled `#F4F4F4`）、`CheckBox`/`RadioButton`、`ScrollBar`、`TabItem`、`Expander` 仍吃系统浅色调色板；两个对话框也仍是系统标题栏。要一致需自绘 ControlTemplate / `DwmSetWindowAttribute`。
- 树节点图标混用 emoji（彩色、不随主题变色、大小不一）与单色字形，可统一成 `Segoe MDL2 Assets`。
- `SettingsWindow` / `DeviceWizardWindow` 的按钮没有走主题样式（`BtnBase` 等样式定义在 `MainWindow.Resources`，不在 `App.xaml`），深色下是浅灰底。
- **设备 DLL 目录的自动探测没覆盖当前布局**：`DeviceDirectoryResolver.Candidates` 只有 `<exe>\Bin` / `..\..\..\Bin` / `..\..\Bin` / `..\Bin`，判据是"目录里有任意 `*.dll`"。exe 在 `src\Bin` 时 `..\Bin` 正好命中 `src\Bin` 本身（里面有 NuGet 的 DLL）→ 一旦 `settings.json` 丢失或换台机器，就会把 `src\Bin` 当成设备目录，扫描不到 `OptoFidelity.*`，报"无法匹配机型 DLL"。现在没暴露只是因为设置里的 `DefaultDeviceDirectory` 兜着。修法：`Candidates` 里加 `MotionAPI`，或把判据改成"优先选含 `OptoFidelity.*.dll` 的目录"。
- **设备 DLL 位于构建输出目录内部**（`src\Bin\MotionAPI\`），而 `Bin\` 已被 `.gitignore` 忽略：VS 的「清理」、手动删 `bin`、`git clean -xdf` 都会把设备 DLL 一起删掉，而这些厂商二进制（`LTSMC.dll` 除外）没有可靠的重取来源。建议挪到不参与构建清理的位置，或至少另存一份备份。
- Debug 与 Release 的输出目录不对称：Debug 是 `src\Bin\`（与设备库子目录 `MotionAPI\` 同级），Release 仍是 `src\bin\Release\`（`csproj:33`）。要统一改 Release 的 `OutputPath` 即可。

---

## 10. 文件地图

**Services（18）**

| 文件 | 职责 |
|---|---|
| `ReflectionEnumerator` | 容错枚举类型/方法/构造函数/属性/字段，标记 logger 参数 |
| `ApiInvoker` | 实例解析 + 参数装配 + logger 注入 + 反射调用 + 结果判定 |
| `DependencyResolver` | `AssemblyResolve` 实际逻辑、搜索路径、实例缓存、依赖体检 |
| `CosturaActivator` | 主动激活内嵌 Costura 解析器、枚举内嵌程序集名 |
| `NativeSearchPath` | `SetDllDirectory` 挂设备目录（原生 DLL 搜索） |
| `NativeDllInspector` | 手工 PE 解析：架构 + 导出表 + P/Invoke 模板 |
| `DeviceDirectoryScanner` / `DeviceDirectoryResolver` | DLL 角色识别 / 设备目录探测（不写死绝对路径） |
| `MachineTypeReader` / `MachineTypeMatcher` | 读 `MachineType.json` / Jaccard 匹配机型与机型 DLL |
| `HistoryService` / `ParameterMemory` / `SettingsService` / `DeviceManager` | 历史（按设备隔离）/ 参数记忆 / 设置 / 设备清单 |
| `ThemeManager` | Light/Dark/系统 解析，主题字典挂应用级 |
| `LogTextBuffer` | 并发入队 + 行数截断，供 UI 定时器取 |
| `AssemblyLoader` / `LogService` | **当前未被使用**（见 §9） |

**ViewModels（11）**

| 文件 | 职责 |
|---|---|
| `MainViewModel`（7 个 partial） | 服务/字段/属性/命令 + 构造装配；`.Device` 加载与设备 CRUD；`.Invocation` 调用与选中态；`.Tree` 搜索过滤；`.NativeDll` 原生 DLL；`.Theme` 主题；`.Log` 日志与导出 |
| `TreeBuilder` | 纯数据转换：Assembly → Namespace → Type → 分组 → 成员，搜索裁剪 |
| `TreeNodeVm` | 树节点数据模型（含 `IsExpanded` 用于展开/折叠所有） |
| `RelayCommand` | 转发 `CommandManager.RequerySuggested` 的命令实现 |
| `Converters` | 只保留被 XAML 真正引用的 3 个转换器 |

**Models（8）**：`ApiAssembly`/`ApiType`、`ApiMethod`、`ApiProperty`/`ApiField`、`DeviceProfile`、`CallHistoryItem`、`DependencyInfo`、`NativeDllInfo`、`DllRole`。

**Views / 根**：`MainWindow.xaml(.cs)`（界面 + 快捷键 + 窗口外壳 + 结果卡片）、`App.xaml(.cs)`（应用级样式 + 崩溃兜底）、`Views/SettingsWindow`、`Views/DeviceWizardWindow`、`Themes/LightTheme.xaml`、`Themes/DarkTheme.xaml`。

---

## 11. 文档分工

| 文档 | 面向 | 内容 |
|---|---|---|
| `README.md` | 使用者 | 怎么装、怎么用、快捷键、落盘路径、已知限制 |
| `OVERVIEW.md`（本文） | 开发者 | 结构、机制、数据流、取舍、技术债 |
| `CLAUDE.md` | AI 协作 / 改代码前 | 硬性约束清单、服务清单、调用链路、构建与校验命令 |
