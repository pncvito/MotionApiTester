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
漏登记不会报错，只会**静默不参与编译**（`Properties\AssemblyInfo.cs` 就漏了很久，导致程序集版本号与
`[assembly: ThemeInfo]` 一直没生效，现已补上）。改完请跑 `python tools/verify-structure.py` 核对。

### 4. 不引入第三方 DI
OptoFidelity 内部用 Prism.DryIoc，工具只用构造函数注入。

### 5. 界面颜色一律走主题令牌
`MainWindow.xaml` / code-behind 里**不要硬编码颜色**，用 `{DynamicResource XxxBrush}`。
两套主题 `Themes/LightTheme.xaml` 与 `Themes/DarkTheme.xaml` 的键必须**完全对称**（当前各 54 个键）。

**主题字典挂在 `Application.Resources` 上**（见 `ThemeManager.Apply`），不是 `MainWindow.Resources` ——
设置窗口 / 设备向导都是独立的 Window，窗口级字典照不到它们，那样它们只能硬编码颜色、也永远不跟主题
（历史上就是这样）。次要文字用 `SubTextBrush`（别再写 `Foreground="Gray"`），分隔线用 `BorderBrush`
（别再写 `#E0E0E0`），提示框用 `WarnBg/WarnFg`。树节点图标颜色也不在 `TreeBuilder` 里写死，
而是由 `MainWindow.xaml` 的节点模板按 `NodeKind` / `Badge` 用 DataTrigger 映射到令牌。

**输入类控件（TextBox / ListBox / CheckBox / RadioButton / GroupBox …）必须在 `App.xaml` 里显式给颜色**（已有隐含样式）：WPF 默认模板用的是
系统色，不跟本应用的主题走 —— 不写就会出现"深色主题里冒出白色输入框"或"深色底 + 黑字看不见"。
`CheckBox` / `RadioButton` 尤其容易漏（它们的 `Foreground` 默认取系统色），
受害点是设置窗口的主题三选一 / 导出格式二选一、以及主窗口参数区的「传 null」——
只覆盖 `Foreground` 即可，勾选框本身的标记由系统模板绘制，换它要整个 `ControlTemplate`。
**`ComboBox` 除外且不要使用**：它的默认模板无视 `Background`（要换整个 `ControlTemplate` 才跟得上主题），
所以本应用已把它清掉 —— 导出格式改用单选按钮、设备向导的机型 DLL 改用 ListBox。

### 6. 程序集只加载一次：`Assembly.Load(byte[])` 不去重
实测（.NET Framework 4.8）：同一份字节 `Assembly.Load` 两次会得到**两个程序集对象**
（`ReferenceEquals` 与各自的类型对象都不相等）。已加载的程序集永不卸载，所以每次重复加载
都是一份永久泄漏的副本 —— 机型 DLL 约 5MB，`Assembly.Load(bytes).GetName().Name` 这种
"只为取个名字"的写法一次就能漏掉两份；`AssemblyResolve` 里每次命中都 `Assembly.Load(bytes)` 同理。

约定：设备 DLL 只 `Load` 一次；取简名用 `AssemblyName.GetAssemblyName(path).Name`（只读元数据、不加载）；
把**实例**登记给 `DependencyResolver.Register(name, assembly)`，让 AssemblyResolve 原样交回同一个程序集。

> 注：byte[] 加载的程序集**已经加载**时，CLR 对引用解析会按标识复用它 —— 实测机型类型继承链上的基类
> 与枚举用的那份确实是同一个对象，并未出现类型身份分裂。所以这条例约解决的是"别造冗余副本、
> 别让交回去的对象不确定"，**不是**因为必然会有 `IsAssignableFrom` 恒 false 的问题。
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
报 CS0234 / CS0246，这不是代码问题。需要编译校验时改用 **`tools/Verify.csproj`**
（SDK 风格，glob 复用 `..\src\**\*.cs` 与 `src\obj\**\*.g.cs`）：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "D:\MotionApiTester\tools\Verify.csproj" -c Debug --nologo
```

它只回答"src 下的 C# 现在还能不能编译通过"，**不产出正式产物**；产物落在 `tools\bin` / `tools\obj`（已被 gitignore）。
注意它**不校验 XAML**，XAML 改动要用下面的脚本另外检查。

### 结构一致性校验

编译管不到的东西（**绑定静默失效、csproj 漏登记**）用这个脚本兜：

```bash
python tools/verify-structure.py
```

⚠️ 当前状态：`tools/verify-structure.py`、`tools/verify-ico.py` 与 `tools/Verify.csproj` **都已存在**
（两个 .py 从 `aab4724^` 恢复，并修了「Windows 控制台非 UTF-8 时中文输出抛 `UnicodeEncodeError`」
与「`src\Bin` 首字母大写导致跳过判断失效」两个问题，详见各自文件头部注释）。

它会检查：XAML 里每个 `{Binding Xxx}` 的根标识符是否有对应的 public 成员、csproj 的
`Compile`/`Page` 清单与磁盘文件是否互相覆盖、已删除的成员是否还有残留引用、XAML 事件处理器
是否有对应实现，并列出文件规模。**改动 XAML 绑定或增删源文件后必跑。**

### 图标校验

`src/app.ico` 曾经**整个目录表损坏**（像素数据完好，但 ICONDIRENTRY 的 `dwBytesInRes`
全被写成 1、`dwImageOffset` 写成 118,119,120… 递增）。Windows 资源管理器对坏目录比较宽容，
肉眼看不出来；而 WPF 的 ImageSource 走 WIC，读目录拿到垃圾后直接抛 `XamlParseException` +
`FileFormatException`（`0x88982F60` 图像无法识别），表现为**程序一启动就崩**。换图标后必跑：

```bash
python tools/verify-ico.py
```

纯标准库，按 ICO 规范校验目录表与数据区是否自洽，无需第三方依赖。
判断「WIC 到底能不能读某张图」最直接的办法，是用 WPF 的
`BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)`
读一次 —— 运行时抛的就是它。

### 图标资源的两个登记点

`src/app.ico` 同时承担两个角色，**缺一不可**：

| csproj 项 | 作用 | 缺失后果 |
|---|---|---|
| `<ApplicationIcon>app.ico</ApplicationIcon>` | exe 文件图标（资源管理器 / 任务栏） | exe 显示系统默认图标 |
| `<Resource Include="app.ico" />` | 打成 WPF 资源，供 XAML 引用 | `Icon="app.ico"` 启动即抛 `XamlParseException` |

引用点在 `MainWindow.xaml`：`Window` 的 `Icon="app.ico"` + 自绘标题栏左侧的
`<Image Source="app.ico"/>`。换图标文件后两者都会自动生效，但要**重新构建**才会重新打包资源。

## 目录结构

```
D:\MotionApiTester\
├─ doc\                     厂家培训资料（与上位机对接文档等，未入库）
├─ src\                     工程本体
│  ├─ MotionApiTester.sln
│  ├─ MotionApiTester.csproj
│  ├─ Bin\                  Debug 构建输出（csproj 的 OutputPath，已 gitignore）
│  │  └─ MotionAPI\         设备 DLL（厂商二进制，未入库；settings.json 的 DefaultDeviceDirectory 指向这里）
│  ├─ App.xaml(.cs)         全局异常兜底 → %TEMP%\MotionApiTester\crash.log
│  ├─ MainWindow.xaml(.cs)  主界面 + IL 签名 / 结果卡片
│  ├─ Models\               ApiMethod / ApiProperty(ApiField) / ApiAssembly(ApiType) / DeviceProfile / ...
│  ├─ Services\             见下
│  ├─ Themes\               LightTheme.xaml / DarkTheme.xaml
│  ├─ ViewModels\           MainViewModel(7 个 partial) / TreeBuilder / RelayCommand / TreeNodeVm / Converters
│  └─ Views\                SettingsWindow / DeviceWizardWindow
└─ tools\                   校验脚本（verify-structure.py / verify-ico.py）+ 编译校验工程（Verify.csproj，产物在 tools\bin / tools\obj，已 gitignore）
```

## 架构说明

### MVVM
- `MainViewModel : ObservableObject`，属性手写（见硬性约束 3）
- **`MainViewModel` 按职责拆成 7 个 partial 文件，新增成员请按职责放进对应文件，不要往核心文件里堆**：
  `MainViewModel.cs`（服务/字段/属性/命令 + 构造装配）、`.Device.cs`、`.Invocation.cs`、
  `.Tree.cs`、`.NativeDll.cs`、`.Theme.cs`、`.Log.cs`
- 自定义 `RelayCommand`（独立文件），通过 `CommandManager.RequerySuggested` 自动重查 CanExecute
- 关键：**`SelectedMethod` / `SelectedProperty` / `SelectedField` 的 setter 必须调用 `CommandManager.InvalidateRequerySuggested()`**，
  否则"调用方法"按钮的可用状态不刷新
- **新增/删除 `public` 成员后，务必核对 `MainWindow.xaml` 里的 `{Binding X}`**：
  partial 拆分与重命名不会报编译错误，绑定失效是静默的（只会表现为界面空白）

### 核心服务（`Services/`）
| 服务 | 职责 |
|------|------|
| `DeviceDirectoryScanner` | 扫描目录，按命名规则识别 DLL 角色 |
| `DeviceDirectoryResolver` | 解析设备 DLL 目录：`DefaultDeviceDirectory` 优先，否则按 exe 相对位置探测（`<exe>\Bin` → 上三级 `Bin` → 上一级 `Bin`） |
| `MachineTypeReader` | 读 `D:\MotionConfig\ConfigHardware\MachineType.json` |
| `MachineTypeMatcher` | Jaccard 相似度匹配机型字符串与 `OptoFidelity.{Model}.dll` |
| `ReflectionEnumerator` | 容错枚举类型 / 方法 / **构造函数** / 属性 / 字段 |
| `ApiInvoker` | 实例解析 + 参数装配 + logger 注入 + 反射调用；日志写进 `LogTextBuffer` |
| `DependencyResolver` | `AssemblyResolve` 的实际逻辑：字节缓存 → 搜索路径（**代码里不写死绝对路径**） |
| `LogTextBuffer` | 日志并发入队 + 保留行截断，供 UI 定时器 Flush |
| `ThemeManager` | Light/Dark/System 解析与主题字典替换，回调同步 `IsDarkTheme` |
| `NativeDllInspector` | PE 解析：架构识别 + **导出表枚举**（名字/序号/转发/调用约定）+ 逐函数 P/Invoke 模板；**只读文件，绝不 LoadLibrary** |
| `ParameterMemory` | 方法参数值记忆（`param-values.json`，按方法签名索引；成功调用后写入） |
| `DeviceManager` / `HistoryService` / `SettingsService` | 持久化到 `%APPDATA%\MotionApiTester\` |

### 调用链路
1. `MainViewModel.LoadDeviceFromDirectory()`
   → 扫描目录 → 匹配机型 DLL → `File.ReadAllBytes` → `Assembly.Load(byte[])`（**每种 DLL 只加载一次**）
   → **实例**登记进 `DependencyResolver.Register`，供 `AssemblyResolve` 原样交回同一个程序集（见硬性约束 6）
   → `ReflectionEnumerator.Enumerate()`
   → `_invoker.SetCandidateAssemblies(_loadedAssemblies)`
2. `InvokeSelectedAsync()` → `ApiInvoker.InvokeAsync(method, JsonArgsOverride)`
3. `ApiInvoker` 内部顺序：
   - `BuildParameters()`：logger 参数（`Action<string>`）注入回调 → 用户勾选的 `PassNull` → `DefaultValue` → `ConvertValue` 逐参数转换；
     若填了 JSON 数组则**按位置整组覆盖**（个数必须与参数个数一致，否则回退）
   - `out` / `ref` 参数按**元素类型**取默认值（`Boolean&` 之类不能传 null，否则 Invoke 直接抛 ArgumentException）；
     逐参数输入与 JSON 都支持数组（文本用逗号分隔 / JSON 用数组），**转换失败会明确打一条警告**而不是静默给 null
   - 构造函数：`ConstructorInfo.Invoke(args)` 返回新实例，并**登记为该类型的当前实例**（后续调用复用它）
   - 实例方法：`ResolveInstance(DeclaringType)` —— ① 先在已建实例里找"能赋给该类型"的那个（`FindReusable`）；
     ② 否则在候选程序集里找**派生得最深**的可无参构造实现（接口 / 抽象类 / 具体基类一视同仁），结果缓存
   - ⚠️ 为什么必须复用同一个对象：设备侧只有一个实例，但成员按声明类型分散在继承链上
     （`BaseInterface.InitializeFixture` 与 `EolSeriesBaseInterface.FixtureMoveToLoadUnloadingPosition`），
     而 `ReflectionEnumerator` 用 `DeclaredOnly`，成员只会挂在声明它的类型节点下 ——
     按声明类型各建各的实例，初始化就永远作用不到动作方法上
   - `Target.Invoke(instance, args)`，返回值 / out-ref 回填值 / 异常全部写进 `CallHistoryItem` + 日志队列
   - 结果判定：先看是否抛异常，再看返回值是否形如 `(false, "…")` —— 设备 API 普遍把失败包在返回值里
     （`TryReadApiFailure`），只看异常会把失败报成"✅ 调用成功"
   - 可选超时（设置的"调用超时"）：超时只让界面不再干等，**无法中断设备侧执行**

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
   右 320px  标签页：调用历史 | 原生 DLL（DLL 列表 + 导出函数 + P/Invoke 模板）
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
| `src\Bin\MotionAPI\` | 设备 DLL 目录（本机 `settings.json` 的 `DefaultDeviceDirectory` 指向这里）。解析顺序：`DefaultDeviceDirectory` 配了且目录存在就用它，否则由 `DeviceDirectoryResolver` 按 exe 相对位置探测（**不写死绝对路径**） |
| `D:\MotionConfig\ConfigHardware\MachineType.json` | 机型字符串（只读） |
| `%APPDATA%\MotionApiTester\settings.json` | 主题 / 日志行数 / 超时 / `DefaultDeviceDirectory`（留空=自动探测）/ `ExtraDependencySearchPaths`（额外依赖搜索目录） |
| `%APPDATA%\MotionApiTester\devices.json` | 已登记设备（`DeviceManager`） |
| `%APPDATA%\MotionApiTester\devices\<机型 DLL 名>\history.json` | 调用历史，**按设备隔离**（首次自动从旧的 `default` 迁移过来） |
| `%APPDATA%\MotionApiTester\param-values.json` | 方法参数值记忆（按方法签名索引，成功调用后写入） |
| `%TEMP%\MotionApiTester\crash.log` | 未处理异常 |

`.gitignore` 忽略了 `Bin\` 与 `src\bin` / `src\obj`，因此**克隆后需要手动把设备 DLL 拷进 `Bin\`**。

## 当前状态

**已实现**
- DLL 扫描 / 角色识别 / 机型模糊匹配 / byte[] 加载 + AssemblyResolve 兜底
- 反射枚举：类型 / 方法 / 构造函数 / 属性 / 字段，过滤编译器生成成员
- API 三栏树 + 搜索过滤 + 右键复制 + 展开折叠
- 原生 DLL **导出表解析**（手工按 PE 规范解析导出目录，只读文件、绝不 LoadLibrary）+ 选中导出函数即生成 P/Invoke 模板
- 方法**参数值记忆**：成功调用后记住参数值，选中方法时自动回填空着的参数，另有「🕘 回填上次」按钮
- 调用历史**按设备隔离**（`devices\<机型 DLL 名>\history.json`；首次从旧的 `default` 自动迁移）
- 方法 / 构造函数 / 属性 / 字段调用（含契约类型实现自动解析、跨继承链复用实例、logger 注入、JSON 整组参数、传 null）
- 调用结果判定：异常 + 返回值自报失败（`(false, "…")`）两条都判；`out`/`ref` 回填值一并显示
- 实时日志面板（队列 + 100ms 刷新）+ 调用历史（持久化，属性/字段读取也记）
- 导出支持纯文本 / JSON（含调用历史），跟随设置里的"导出格式"
- 原生 DLL 面板（PE 架构识别 + P/Invoke 模板）
- 多设备登记与切换（`devices.json`）
- 浅色 / 深色 / 跟随系统主题（语义令牌，界面零硬编码颜色，主题字典挂应用级 → 三个窗口一起生效）
- 设置窗口（主题 / 日志行数 / 导出格式 / 调用超时 / 设备目录 / 额外依赖搜索路径 / 设备管理）、全局异常兜底
- `MainViewModel` 按职责拆为 7 个 partial 文件；树构建 / 主题 / 依赖解析 / 日志缓冲 / 目录解析已抽成独立服务

**未实现 / 已知限制**
- 导出表里**只有名字与序号，没有签名**（参数类型 / 返回值）—— 生成的模板只是骨架，必须照厂商 `.h` 核对
- 参数记忆只记**成功调用**用过的值（失败多半是参数本身有问题，记下来会一直沿用错的）；
  存在 `%APPDATA%\MotionApiTester\param-values.json`，键 = 声明类型全名 + `MethodBase.ToString()`
  （注意 `MethodBase.ToString()` **不含声明类型**，不加前缀会让不同类同名同参的方法串味）
- 因此**接口节点与类节点各记各的**：`IBaseInterface.InitializeFixture` 与 `BaseInterface.InitializeFixture`
  是两套参数记忆（树的 DeclaredOnly 会同时列出这两处，看起来是同一个功能）
- 树的类型图标是彩色 emoji（🟦🟩🟧），颜色由字体决定、不随主题变；
  只有单色字形（⚙ ⚡ ⊞ ▣ 📦）的 Foreground 跟随主题令牌
- "取消调用"只能取消尚未开始的任务；`MethodBase.Invoke` 是同步阻塞调用，无法中断已在设备侧执行的指令。
  "调用超时"同理：只让界面不再干等并明确提示，设备侧仍在跑 —— **不要因为超时就重复下发动作指令**
- JSON 高级参数**不支持对象类型**（数组已支持），遇到对象会明确报一条警告并按 null 传入
- 依赖缺失报告延迟约 400ms 裁决（AssemblyResolve 是多播事件，单个处理器未命中≠解析失败，见 `DependencyResolver.FlushPendingMisses`）
- "卸载"只清界面与缓存：`Assembly.Load` 无法从 AppDomain 卸载
- 接口实现必须**可无参构造**，否则仍会失败（会给出明确错误信息）
- 搜索每次按键全量重建整棵树，大程序集（5MB Costura）下会卡顿
